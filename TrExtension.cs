using System;
using System.Windows.Markup;
using CallAudioRecorder.Services;

namespace CallAudioRecorder;

/// <summary>
/// Надпись на языке интерфейса прямо в XAML: <c>{l:Tr 'Итоги', En='Summary'}</c>.
/// Значение берётся один раз, при загрузке разметки, — поэтому смена языка пересоздаёт окно
/// (<c>MainWindow.SwitchUiLanguage</c>), а не перерисовывает надписи на месте.
///
/// Строки с запятыми берутся в одинарные кавычки, иначе парсер разметки разрежет их
/// на аргументы; апострофа в английском тексте избегаем по той же причине.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TrExtension : MarkupExtension
{
    public TrExtension() { }

    public TrExtension(string ru) => Ru = ru;

    [ConstructorArgument("ru")]
    public string Ru { get; set; } = "";

    public string En { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.T(Ru, En);
}
