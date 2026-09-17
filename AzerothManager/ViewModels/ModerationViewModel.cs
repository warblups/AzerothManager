using System.Collections.ObjectModel;
using AzerothManager.Models;
using AzerothManager.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AzerothManager.ViewModels;

/// <summary>
/// Modération (§9) : la vue d'ensemble des sanctions. Le module Comptes agit compte par
/// compte ; celui-ci répond à « qui est sanctionné, par qui, et pourquoi ».
/// </summary>
public partial class ModerationViewModel : ObservableObject
{
    private readonly ModerationService _moderation;
    private readonly ServerContext _context;

    public ObservableCollection<Sanction> Sanctions { get; } = [];

    public NamedValue[] Targets { get; } =
    [
        new(0, "Compte"), new(1, "Personnage"), new(2, "Adresse IP"), new(3, "Compte d'un personnage")
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private Sanction? _selected;

    [ObservableProperty] private bool _activeOnly = true;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "Chargez les sanctions.";

    [ObservableProperty] private NamedValue? _target;
    [ObservableProperty] private string _targetName = "";
    [ObservableProperty] private string _duration = "1d";
    [ObservableProperty] private string _reason = "";

    public bool HasSelection => Selected is not null;

    public ModerationViewModel(ModerationService moderation, ServerContext context)
    {
        _moderation = moderation;
        _context = context;
        Target = Targets[0];
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (!_context.HasActive)
        {
            Status = "Aucun serveur actif.";
            return;
        }

        IsBusy = true;
        try
        {
            Sanctions.Clear();
            foreach (var s in await _moderation.SanctionsAsync(ActiveOnly)) Sanctions.Add(s);

            Status = Sanctions.Count == 0
                ? ActiveOnly ? "Aucune sanction en cours." : "Aucune sanction enregistrée."
                : $"{Sanctions.Count} sanction(s)";
        }
        catch (Exception ex)
        {
            Status = "Erreur : " + ex.Message;
        }
        finally { IsBusy = false; }
    }

    partial void OnActiveOnlyChanged(bool value) => _ = LoadAsync();

    private async Task RunAsync(Func<Task<GmCommandResult>> action, string label)
    {
        IsBusy = true;
        Status = label + "…";
        try
        {
            var r = await action();
            Status = r.Success
                ? $"{label} : {r.Output.ReplaceLineEndings(" ").Trim()}"
                : $"{label} a échoué — {r.Output}";
            if (r.Success) await LoadAsync();
        }
        catch (Exception ex)
        {
            Status = $"{label} a échoué — {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task ApplyAsync()
    {
        if (string.IsNullOrWhiteSpace(TargetName))
        {
            Status = "Indiquez la cible de la sanction.";
            return;
        }

        var name = TargetName.Trim();
        var duration = string.IsNullOrWhiteSpace(Duration) ? "0" : Duration.Trim();
        var reason = string.IsNullOrWhiteSpace(Reason) ? "Sans motif" : Reason.Trim();
        var permanent = duration is "0" or "-1";

        var quoi = Target?.Value switch
        {
            1 => $"le personnage « {name} »",
            2 => $"l'adresse IP {name}",
            3 => $"le compte du personnage « {name} »",
            _ => $"le compte « {name} »"
        };

        // Une adresse IP est souvent partagée : le dire avant, pas après.
        var avertissement = Target?.Value == 2
            ? "\n\nUne adresse IP est souvent partagée entre plusieurs joueurs, " +
              "voire tout un foyer : la sanction les touchera tous."
            : "";

        var confirm = System.Windows.MessageBox.Show(
            $"Bannir {quoi} {(permanent ? "définitivement" : $"pour {duration}")} ?\n\n" +
            $"Motif : {reason}{avertissement}",
            "Confirmation de sanction",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.No);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        Func<Task<GmCommandResult>> action = Target?.Value switch
        {
            1 => () => _moderation.BanCharacterAsync(name, duration, reason),
            2 => () => _moderation.BanIpAsync(name, duration, reason),
            3 => () => _moderation.BanPlayerAccountAsync(name, duration, reason),
            _ => () => _moderation.BanAccountAsync(name, duration, reason)
        };

        await RunAsync(action, $"Bannissement de {name}");
    }

    [RelayCommand]
    private async Task LiftAsync()
    {
        if (Selected is null) return;
        var kind = Selected.Kind;
        var target = Selected.Target;
        await RunAsync(() => _moderation.UnbanAsync(kind, target), $"Levée de la sanction sur {target}");
    }

    [RelayCommand]
    private async Task DetailsAsync()
    {
        if (Selected is null) return;
        var kind = Selected.Kind;
        var target = Selected.Target;
        await RunAsync(() => _moderation.BanInfoAsync(kind, target), $"Détail de la sanction sur {target}");
    }
}
