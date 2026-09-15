using System.Collections.ObjectModel;
using System.Text;
using AzerothManager.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AzerothManager.ViewModels;

/// <summary>
/// Console de commandes GM (§16). Elle n'est pas isolée : les modules des §9 à §11
/// s'appuieront sur le même <see cref="GmCommandService"/>, et la commande envoyée
/// reste affichée avec la réponse du serveur (§17).
/// </summary>
public partial class GmConsoleViewModel : ObservableObject
{
    private readonly GmCommandService _gm;
    private readonly ServerContext _context;
    private readonly List<string> _history = [];
    private int _historyIndex = -1;

    [ObservableProperty] private string _command = "";
    [ObservableProperty] private bool _isBusy;

    public ObservableCollection<string> Output { get; } = [];

    /// <summary>Commandes fréquentes, proposées en accès direct.</summary>
    public string[] Shortcuts { get; } =
    [
        "server info", "account onlinelist", "gm list", "ticket list", "server motd"
    ];

    public GmConsoleViewModel(GmCommandService gm, ServerContext context)
    {
        _gm = gm;
        _context = context;
        Output.Add("Console GM — SOAP. Saisissez une commande sans le point initial, ou avec, les deux passent.");
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        if (string.IsNullOrWhiteSpace(Command) || IsBusy) return;

        if (!_context.HasActive)
        {
            Output.Add("✖ Aucun serveur actif. Sélectionnez un profil dans Configuration des serveurs.");
            return;
        }

        var sent = Command.Trim();
        _history.Add(sent);
        _historyIndex = _history.Count;
        Command = "";

        Output.Add($"> .{sent.TrimStart('.')}");
        IsBusy = true;
        try
        {
            var r = await _gm.ExecuteAsync(sent);
            var text = string.IsNullOrWhiteSpace(r.Output) ? "(aucune sortie)" : r.Output;
            var sb = new StringBuilder();
            foreach (var line in text.Split('\n'))
                sb.AppendLine((r.Success ? "  " : "✖ ") + line.TrimEnd('\r'));
            Output.Add(sb.ToString().TrimEnd());
            Output.Add($"  [{r.ElapsedMs} ms]");
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void Clear() => Output.Clear();

    [RelayCommand]
    private void UseShortcut(string? shortcut)
    {
        if (!string.IsNullOrWhiteSpace(shortcut)) Command = shortcut;
    }

    /// <summary>Rappel de l'historique par les flèches haut / bas.</summary>
    public void RecallHistory(int direction)
    {
        if (_history.Count == 0) return;
        _historyIndex = Math.Clamp(_historyIndex + direction, 0, _history.Count);
        Command = _historyIndex < _history.Count ? _history[_historyIndex] : "";
    }
}
