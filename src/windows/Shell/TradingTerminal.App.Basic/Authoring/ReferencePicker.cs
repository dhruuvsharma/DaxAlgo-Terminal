using Microsoft.Win32;

namespace TradingTerminal.App.Authoring;

/// <summary>
/// The file dialog behind the authoring pane's "Add a reference" button.
///
/// <para>It lives in the shell rather than in the view-model's project for the reason every dialog in
/// this codebase does: that project is WPF-free, and a view-model that opened a window could not be
/// tested without one.</para>
/// </summary>
public sealed class ReferencePicker : IReferencePicker
{
    public Task<IReadOnlyList<string>> PickImagesAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Pick reference pictures — what this unit should look like",
            Multiselect = true,

            // The three types every vision provider accepts. Offering more would let a user attach a
            // BMP and be told afterwards that it could not be used.
            Filter = "Pictures (*.png;*.jpg;*.jpeg;*.webp)|*.png;*.jpg;*.jpeg;*.webp",
            CheckFileExists = true,
        };

        return Task.FromResult<IReadOnlyList<string>>(
            dialog.ShowDialog() == true ? dialog.FileNames : []);
    }
}
