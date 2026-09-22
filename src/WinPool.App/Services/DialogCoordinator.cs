using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WinPool.App.Services;

/// <summary>Serializes modal dialogs which share a WinUI XamlRoot.</summary>
public static class DialogCoordinator
{
    private static readonly ConditionalWeakTable<XamlRoot, SemaphoreSlim> Gates = new();

    public static Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        return dialog.XamlRoot is { } root
            ? ShowAsync(dialog, root)
            : throw new InvalidOperationException("A ContentDialog requires a XamlRoot.");
    }

    public static async Task<ContentDialogResult> ShowAsync(
        ContentDialog dialog,
        XamlRoot xamlRoot)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        ArgumentNullException.ThrowIfNull(xamlRoot);

        var gate = Gates.GetValue(xamlRoot, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            dialog.XamlRoot = xamlRoot;
            return await dialog.ShowAsync();
        }
        finally
        {
            gate.Release();
        }
    }
}
