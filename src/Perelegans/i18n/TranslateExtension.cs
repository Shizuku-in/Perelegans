using System;
using System.Windows.Data;
using System.Windows.Markup;
using Perelegans.Services;

namespace Perelegans.i18n;

[MarkupExtensionReturnType(typeof(string))]
public class TranslateExtension : MarkupExtension
{
    [ConstructorArgument("key")]
    public string Key { get; set; }

    public TranslateExtension(string key)
    {
        Key = key;
    }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        if (string.IsNullOrWhiteSpace(Key))
            return string.Empty;

        var binding = new Binding($"[{Key}]")
        {
            Source = TranslationService.Instance,
            Mode = BindingMode.OneWay
        };

        return binding.ProvideValue(serviceProvider);
    }
}
