using System.Globalization;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Media;
using Avalonia.Threading;
using VaultSync.Core.Models;
using VaultSync.Core.Services;
using VaultSync.UI.Services;

namespace VaultSync.RecoveryController;

internal sealed class RecoveryViewModel : IRecoveryPresentation
{
    private enum Screen { Starting, Folder, Target, Review, Waiting, Progress, Cancelling, Finished }
    private sealed class TargetItem(LinuxRecoveryResponses.Device device, CultureInfo culture) : INotifyPropertyChanged
    {
        private CultureInfo culture = culture;
        public LinuxRecoveryResponses.Device Device { get; } = device;
        public string Label => $"{Device.KernelName}  ({Device.Major}:{Device.Minor})  —  {Device.Capacity.ToString("N0", culture)} B";
        public event PropertyChangedEventHandler? PropertyChanged;
        public void SetCulture(CultureInfo value)
        {
            culture = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label)));
        }
    }
    private readonly RecoveryWindow window;
    private readonly LocalizationService text = new();
    private readonly CancellationTokenSource cancellation = new();
    private readonly ComboBox language;
    private readonly TextBlock heading, description, details;
    private readonly TextBox folder;
    private readonly ListBox targets;
    private readonly Button primary, cancel;
    private readonly ProgressBar progress;
    private Screen screen;
    private TaskCompletionSource<string?>? choice;
    private TaskCompletionSource<bool>? acknowledgment;
    private LinuxRecoveryResponses.Review? review;
    private LinuxRecoverySession.Snapshot? snapshot;
    private LinuxRecoveryControllerClient.Result result;
    private bool terminalObserved;
    internal bool IsComplete { get; private set; }
    public CancellationToken CancellationRequested => cancellation.Token;

    internal RecoveryViewModel(RecoveryWindow window)
    {
        this.window = window;
        language = window.FindControl<ComboBox>("Language")!;
        heading = window.FindControl<TextBlock>("Heading")!;
        description = window.FindControl<TextBlock>("Description")!;
        details = window.FindControl<TextBlock>("Details")!;
        folder = window.FindControl<TextBox>("Folder")!;
        targets = window.FindControl<ListBox>("Targets")!;
        primary = window.FindControl<Button>("Primary")!;
        cancel = window.FindControl<Button>("Cancel")!;
        progress = window.FindControl<ProgressBar>("Progress")!;
        language.ItemsSource = text.SupportedLanguages;
        language.SelectedItem = text.SupportedLanguages[0];
        language.SelectionChanged += (_, _) =>
        {
            if (language.SelectedItem is LanguageOption selected && text.SetLanguage(selected.Code))
            {
                window.FlowDirection = selected.Code == "ar" ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
                if (targets.ItemsSource is IEnumerable<TargetItem> items)
                    foreach (var item in items) item.SetCulture(CultureInfo.GetCultureInfo(selected.Code));
                Render();
            }
        };
        Render();
    }

    public Task<string?> ChooseImageFolderAsync(Guid operation, CancellationToken token) => ChooseAsync(Screen.Folder, null, token);
    public Task<string?> ChooseTargetAsync(Guid operation, LinuxRecoveryResponses.Inventory inventory, CancellationToken token) => ChooseAsync(Screen.Target, inventory, token);
    private Task<string?> ChooseAsync(Screen next, LinuxRecoveryResponses.Inventory? inventory, CancellationToken token)
    {
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = token.Register(() => completion.TrySetCanceled(token));
        _ = completion.Task.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);
        Dispatcher.UIThread.Post(() =>
        {
            if (completion.Task.IsCompleted || terminalObserved || cancellation.IsCancellationRequested || IsComplete) { completion.TrySetResult(null); return; }
            choice?.TrySetResult(null); acknowledgment?.TrySetResult(false);
            choice = completion; acknowledgment = null; screen = next;
            if (inventory is not null)
                targets.ItemsSource = inventory.State == LinuxBlockInventoryState.Observed && !inventory.HasUnresolvedSwaps
                    ? inventory.Devices.Where(d => d.Kind == LinuxBlockKind.WholeDisk && d.Identity == LinuxBlockIdentity.UniqueInSnapshot && d.DiskSequence is > 0 && !d.IsReadOnly && d.Use == LinuxBlockUse.Unknown && !d.MountObserved && !d.SwapObserved && !d.HasHolders).Select(d => new TargetItem(d, CultureInfo.GetCultureInfo(text.CurrentLanguage))).ToArray()
                    : Array.Empty<TargetItem>();
            Render();
        });
        return completion.Task;
    }
    public Task<bool> AcknowledgeReviewAsync(LinuxRecoveryResponses.Review value, CancellationToken token)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = token.Register(() => completion.TrySetCanceled(token));
        _ = completion.Task.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);
        Dispatcher.UIThread.Post(() =>
        {
            if (completion.Task.IsCompleted || terminalObserved || cancellation.IsCancellationRequested || IsComplete) { completion.TrySetResult(false); return; }
            choice?.TrySetResult(null); acknowledgment?.TrySetResult(false);
            choice = null; acknowledgment = completion; review = value; screen = Screen.Review; Render();
        });
        return completion.Task;
    }
    internal void Accept()
    {
        if (screen == Screen.Folder && !string.IsNullOrWhiteSpace(folder.Text))
        { choice?.TrySetResult(folder.Text.Trim()); choice = null; screen = Screen.Starting; }
        else if (screen == Screen.Target && targets.SelectedItem is TargetItem target)
        { choice?.TrySetResult(target.Device.KernelName); choice = null; screen = Screen.Starting; }
        else if (screen == Screen.Review && acknowledgment is not null)
        { acknowledgment.TrySetResult(true); acknowledgment = null; screen = Screen.Waiting; }
        Render();
    }
    internal void Cancel()
    {
        if (IsComplete) return;
        cancellation.Cancel(); choice?.TrySetResult(null); acknowledgment?.TrySetResult(false);
        choice = null; acknowledgment = null; screen = Screen.Cancelling; Render();
    }
    public void Observe(LinuxRecoverySession.Snapshot value) => Dispatcher.UIThread.Post(() =>
    {
        if (IsComplete || terminalObserved) return;
        snapshot = value;
        if (value.Phase is LinuxRecoverySessionPhase.Completed or LinuxRecoverySessionPhase.Refused or LinuxRecoverySessionPhase.Faulted)
        {
            terminalObserved = true; choice?.TrySetResult(null); acknowledgment?.TrySetResult(false);
            choice = null; acknowledgment = null; screen = Screen.Cancelling;
        }
        else if (value.CancellationRequested || cancellation.IsCancellationRequested) { choice?.TrySetResult(null); acknowledgment?.TrySetResult(false); choice = null; acknowledgment = null; screen = Screen.Cancelling; }
        else if (value.Phase == LinuxRecoverySessionPhase.Executing) screen = Screen.Progress;
        Render();
    });
    public void Complete(LinuxRecoveryControllerClient.Result value) => Dispatcher.UIThread.Post(() =>
    {
        if (IsComplete) return;
        result = value; IsComplete = true; terminalObserved = true;
        choice?.TrySetResult(null); acknowledgment?.TrySetResult(false); choice = null; acknowledgment = null;
        screen = Screen.Finished; Render();
    });
    // The wizard's screen, action, result and help keys are defined in every catalog.
    private string T(string key)
    {
        string fullKey = "Recovery.Wizard." + key;
        return text.GetString(fullKey);
    }
    private string F(string key, params object[] args) => string.Format(CultureInfo.GetCultureInfo(text.CurrentLanguage), text.GetString(key), args);
    private void Render()
    {
        window.Title = text.GetString("Nav.Recovery");
        AutomationProperties.SetName(language, text.GetString("Settings.Advanced.LanguageLabel"));
        AutomationProperties.SetName(folder, T("Folder"));
        AutomationProperties.SetName(targets, T("Target"));
        AutomationProperties.SetName(progress, T("Progress"));
        folder.IsVisible = screen == Screen.Folder; targets.IsVisible = screen == Screen.Target;
        primary.IsVisible = screen is Screen.Folder or Screen.Target or Screen.Review;
        primary.Content = T(screen == Screen.Review ? "Acknowledge" : "Continue");
        cancel.Content = text.GetString(IsComplete ? "Common.Close" : "Common.Cancel");
        heading.Text = T(screen.ToString());
        description.Text = screen == Screen.Starting ? text.GetString("Recovery.Loading") : T(screen switch { Screen.Folder => "FolderHelp", Screen.Target => "TargetHelp", Screen.Review => "ReviewHelp", Screen.Waiting => "WaitingHelp", Screen.Progress => "ProgressHelp", Screen.Cancelling => "CancellingHelp", Screen.Finished => result.ToString(), _ => "Starting" });
        details.Text = screen == Screen.Review && review is { } r
            ? string.Join("\n\n", text.GetString("Recovery.Console.Warning"),
                F("Recovery.Console.Image", r.ImageId, r.CaptureId, r.Generation), F("Recovery.Console.Manifest", r.ManifestRoot),
                F("Recovery.Console.Target", r.Target.KernelName, $"{r.Target.Major}:{r.Target.Minor}", r.Target.DiskSequence!),
                F("Recovery.Console.Capacity", r.OverwriteLength, r.Target.LogicalSectorBytes, r.Target.PhysicalSectorBytes))
            : snapshot?.Review is { } current && screen is Screen.Progress or Screen.Cancelling or Screen.Finished
                ? F("Recovery.Wizard.Bytes", snapshot.AcknowledgedBytes, current.OverwriteLength) : "";
        progress.IsVisible = snapshot?.Review is not null && screen == Screen.Progress;
        if (snapshot?.Review is { OverwriteLength: > 0 } observed) progress.Value = (double)snapshot.AcknowledgedBytes * 100 / observed.OverwriteLength;
    }
}
