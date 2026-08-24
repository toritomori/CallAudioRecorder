using System;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.Wave;

namespace CallAudioRecorder.Services;

/// <summary>
/// Захват звука ОДНОГО приложения (Windows Application Loopback): вместо микса всего
/// устройства вывода берётся только то, что играет выбранный процесс и его дочерние.
///
/// Механика: <c>ActivateAudioInterfaceAsync</c> на псевдоустройстве «VAD\Process_Loopback»
/// с <c>AUDIOCLIENT_ACTIVATION_PARAMS</c> (тип PROCESS_LOOPBACK) отдаёт обычный
/// <c>IAudioClient</c>, который отдаёт звук только этого дерева процессов.
///
/// Документация Microsoft требует Windows build 20348+, но на Windows 10 22H2 (19045)
/// API работает — проверено пробником: со звучащего процесса rms ≈ 0.07, с молчащего
/// (explorer) — ровные нули, то есть фильтрация по процессу настоящая, а не общий микс.
/// На более старых системах активация упадёт с E_INVALIDARG — это ловится как
/// <see cref="NotSupportedException"/> при создании.
///
/// Формат фиксирован (48 kHz, 16 бит, stereo): у псевдоустройства нет своего микс-формата,
/// клиент обязан назвать его сам, а движок конвертирует.
/// </summary>
public sealed class ProcessLoopbackCapture : IWaveIn
{
    private const string VirtualLoopbackDevice = "VAD\\Process_Loopback";
    private const int AudioClientSharedMode = 0;
    private const int StreamFlagsLoopback = 0x00020000;
    private const int StreamFlagsEventCallback = 0x00040000;
    private const int BufferFlagsSilent = 0x2;
    private const long BufferDuration = 2_000_000; // 200 мс в единицах по 100 нс

    private static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    private static readonly Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

    private readonly ManualResetEvent _dataReady = new(false);
    private readonly ManualResetEventSlim _activated = new(false);
    private readonly ManualResetEventSlim _startRequested = new(false);
    private readonly Thread _worker;

    private IAudioClient? _client;
    private IAudioCaptureClient? _capture;
    private Exception? _activationError;
    private volatile bool _running;
    private volatile bool _shutdown;

    public WaveFormat WaveFormat { get; set; } = new(48000, 16, 2);

    public event EventHandler<WaveInEventArgs>? DataAvailable;
    public event EventHandler<StoppedEventArgs>? RecordingStopped;

    /// <param name="processId">PID приложения, чей звук нужно записать.</param>
    /// <param name="includeProcessTree">
    /// Включать дочерние процессы. Обязательно для браузеров и Electron-приложений:
    /// окно принадлежит главному процессу, а звук играет отдельный дочерний.
    /// </param>
    public ProcessLoopbackCapture(int processId, bool includeProcessTree = true)
    {
        // Весь жизненный цикл COM-объектов — в одном MTA-потоке: UI-поток WPF живёт в STA,
        // а если MTA-поток создать только на время активации, то с его завершением
        // умирает и последняя ссылка на MTA-апартмент, после чего клиент становится негодным
        // (следующая активация падает с E_UNEXPECTED). Поэтому поток живёт, пока живёт объект.
        _worker = new Thread(() => Worker(processId, includeProcessTree))
        {
            IsBackground = true,
            Name = "ProcessLoopback",
            Priority = ThreadPriority.AboveNormal // не пропускать пакеты под нагрузкой
        };
        _worker.SetApartmentState(ApartmentState.MTA);
        _worker.Start();

        _activated.Wait();
        if (_activationError is not null)
        {
            _shutdown = true;
            _startRequested.Set();
            _worker.Join(TimeSpan.FromSeconds(2));
            throw _activationError;
        }
    }

    /// <summary>Активирует захват, ждёт команды на старт, крутит чтение и сам же освобождает COM.</summary>
    private void Worker(int processId, bool includeProcessTree)
    {
        try
        {
            (_client, _capture) = ActivateCore(processId, includeProcessTree, WaveFormat);
        }
        catch (Exception ex)
        {
            _activationError = ex;
        }
        finally
        {
            _activated.Set();
        }

        try
        {
            if (_activationError is not null) return;

            _startRequested.Wait();
            if (!_shutdown) CaptureLoop();
        }
        finally
        {
            if (_capture is not null && Marshal.IsComObject(_capture)) Marshal.ReleaseComObject(_capture);
            if (_client is not null && Marshal.IsComObject(_client)) Marshal.ReleaseComObject(_client);
            _capture = null;
            _client = null;
        }
    }

    private static (IAudioClient, IAudioCaptureClient) ActivateCore(
        int processId, bool includeTree, WaveFormat format)
    {
        // Аудиодвижок держит предыдущую активацию, пока живы её COM-обёртки, а те уходят
        // только с финализацией. Без этой сборки вторая запись подряд в одном процессе
        // падает на Initialize с E_UNEXPECTED (проверено; пауза не помогает, помогает именно GC).
        GC.Collect();
        GC.WaitForPendingFinalizers();

        IntPtr activationParams = Marshal.AllocHGlobal(12);
        IntPtr propVariant = Marshal.AllocHGlobal(24);
        try
        {
            // AUDIOCLIENT_ACTIVATION_PARAMS: тип 1 = PROCESS_LOOPBACK,
            // затем { TargetProcessId, ProcessLoopbackMode (0 — вместе с деревом процессов) }.
            Marshal.WriteInt32(activationParams, 0, 1);
            Marshal.WriteInt32(activationParams, 4, processId);
            Marshal.WriteInt32(activationParams, 8, includeTree ? 0 : 1);

            // PROPVARIANT с VT_BLOB (65), внутри — указатель на структуру выше.
            for (int i = 0; i < 24; i++) Marshal.WriteByte(propVariant, i, 0);
            Marshal.WriteInt16(propVariant, 0, 65);
            Marshal.WriteInt32(propVariant, 8, 12);
            Marshal.WriteIntPtr(propVariant, 16, activationParams);

            var handler = new ActivationHandler();
            int hr = ActivateAudioInterfaceAsync(VirtualLoopbackDevice, IID_IAudioClient,
                propVariant, handler, out var operation);
            if (hr != 0) throw Unsupported(hr, "не удалось начать активацию");

            if (!handler.Completed.WaitOne(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Windows не ответила на запрос захвата звука приложения.");

            operation.GetActivateResult(out int activateHr, out object activated);
            // Операцию активации нужно отпустить сразу: пока её COM-обёртка жива, аудиодвижок
            // считает активацию незавершённой и следующая попытка в этом же процессе
            // падает с E_UNEXPECTED (проверено — вторая запись подряд не начиналась).
            Marshal.ReleaseComObject(operation);
            if (activateHr != 0 || activated is null) throw Unsupported(activateHr, "активация отклонена");

            var client = (IAudioClient)activated;
            var waveFormat = new WaveFormatEx
            {
                wFormatTag = 1, // PCM
                nChannels = (short)format.Channels,
                nSamplesPerSec = format.SampleRate,
                wBitsPerSample = (short)format.BitsPerSample,
                nBlockAlign = (short)format.BlockAlign,
                nAvgBytesPerSec = format.AverageBytesPerSecond,
                cbSize = 0
            };
            int initHr = client.Initialize(AudioClientSharedMode,
                StreamFlagsLoopback | StreamFlagsEventCallback, BufferDuration, 0, ref waveFormat, IntPtr.Zero);
            if (initHr != 0) throw Unsupported(initHr, "аудиодвижок не принял формат записи");

            var captureId = IID_IAudioCaptureClient;
            if (client.GetService(ref captureId, out object captureObj) != 0 || captureObj is null)
                throw new InvalidOperationException("Не удалось получить поток захвата звука приложения.");

            return (client, (IAudioCaptureClient)captureObj);
        }
        finally
        {
            Marshal.FreeHGlobal(propVariant);
            Marshal.FreeHGlobal(activationParams);
        }
    }

    private static Exception Unsupported(int hr, string what) => new NotSupportedException(
        $"Запись звука отдельного приложения недоступна ({what}, код 0x{hr:X8}). " +
        "Нужна Windows 10 22H2 или новее; выберите вместо этого устройство вывода.");

    public void StartRecording()
    {
        if (_running || _shutdown) return;
        _running = true;
        _startRequested.Set();
    }

    public void StopRecording()
    {
        if (!_running) return;
        _running = false;
        _worker.Join(TimeSpan.FromSeconds(2));
    }

    private void CaptureLoop()
    {
        Exception? error = null;
        var buffer = new byte[WaveFormat.AverageBytesPerSecond]; // запас на секунду
        var client = _client!;
        var capture = _capture!;
        try
        {
            client.SetEventHandle(_dataReady.SafeWaitHandle.DangerousGetHandle());
            client.Start();

            while (_running)
            {
                _dataReady.WaitOne(100);
                while (capture.GetNextPacketSize(out int packetFrames) == 0 && packetFrames > 0)
                {
                    if (capture.GetBuffer(out IntPtr data, out int frames, out int flags, out _, out _) != 0) break;

                    int bytes = frames * WaveFormat.BlockAlign;
                    if (bytes > buffer.Length) buffer = new byte[bytes];
                    // При тишине движок отдаёт буфер с флагом SILENT и неопределённым содержимым.
                    if ((flags & BufferFlagsSilent) != 0 || data == IntPtr.Zero) Array.Clear(buffer, 0, bytes);
                    else Marshal.Copy(data, buffer, 0, bytes);

                    capture.ReleaseBuffer(frames);
                    if (bytes > 0) DataAvailable?.Invoke(this, new WaveInEventArgs(buffer, bytes));
                }
            }

            client.Stop();
        }
        catch (Exception ex)
        {
            error = ex;
        }
        RecordingStopped?.Invoke(this, new StoppedEventArgs(error));
    }

    public void Dispose()
    {
        StopRecording();
        _shutdown = true;
        _startRequested.Set(); // если запись так и не начиналась — выпустить поток из ожидания
        _worker.Join(TimeSpan.FromSeconds(2));
        _dataReady.Dispose();
        _activated.Dispose();
        _startRequested.Dispose();
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true)]
    private static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation
    {
        void GetActivateResult([MarshalAs(UnmanagedType.Error)] out int activateResult,
            [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
    }

    private sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler
    {
        public readonly ManualResetEvent Completed = new(false);
        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation) => Completed.Set();
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct WaveFormatEx
    {
        public short wFormatTag;
        public short nChannels;
        public int nSamplesPerSec;
        public int nAvgBytesPerSec;
        public short nBlockAlign;
        public short wBitsPerSample;
        public short cbSize;
    }

    // Порядок методов повторяет IAudioClient из Audioclient.h — менять его нельзя.
    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig]
        int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity,
            ref WaveFormatEx format, IntPtr audioSessionGuid);
        int GetBufferSize(out int bufferSize);
        long GetStreamLatency();
        int GetCurrentPadding(out int padding);
        [PreserveSig]
        int IsFormatSupported(int shareMode, ref WaveFormatEx format, IntPtr closestMatch);
        int GetMixFormat(out IntPtr format);
        int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        int Start();
        int Stop();
        int Reset();
        int SetEventHandle(IntPtr handle);
        [PreserveSig]
        int GetService(ref Guid interfaceId, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig]
        int GetBuffer(out IntPtr data, out int numFramesToRead, out int bufferFlags,
            out long devicePosition, out long qpcPosition);
        [PreserveSig]
        int ReleaseBuffer(int numFramesRead);
        [PreserveSig]
        int GetNextPacketSize(out int numFramesInNextPacket);
    }
}
