#nullable enable
extern alias RecoveryController;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Platform;
using Avalonia.Threading;
using VaultSync.Core.Models;
using VaultSync.Core.Services;
using RecoveryApp = RecoveryController::VaultSync.RecoveryController.RecoveryApp;
using RecoveryWindow = RecoveryController::VaultSync.RecoveryController.RecoveryWindow;
using RecoveryX11Focus = RecoveryController::VaultSync.RecoveryController.LinuxRecoveryX11Focus;
using LanguageOption = RecoveryController::VaultSync.UI.Services.LanguageOption;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class RecoveryControllerPresentationTests
{
    private static LinuxRecoveryResponses.Device Device() => new("fixture-disk", null, 8, 16, 1234432, 42, 512, 512,
        LinuxBlockKind.WholeDisk, LinuxBlockIdentity.UniqueInSnapshot, LinuxBlockUse.Unknown, false, false, false, false);
    private static LinuxRecoveryResponses.Inventory Inventory(LinuxRecoveryResponses.Device device) =>
        new(Guid.NewGuid(), DateTimeOffset.UtcNow, LinuxBlockInventoryState.Observed,
            LinuxBlockInventoryReason.NamespaceObservationOnly, false, [device]);
    private static LinuxRecoveryResponses.Review Review(LinuxRecoveryResponses.Device device, LinuxRecoveryResponses.Inventory inventory) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, new string('a', 64),
            DateTimeOffset.UtcNow, device.Capacity, device, inventory.ObservationId);
    private static Task Flush() => Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background).GetTask();
    private static void Click(RecoveryWindow window, string name) =>
        window.FindControl<Button>(name)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static string Label(object item) => (string)TypeDescriptor.GetProperties(item)["Label"]!.GetValue(item)!;
    private static async Task Finish(RecoveryWindow window, LinuxRecoveryControllerClient.Result result)
    {
        window.ViewModel.Complete(result);
        await Flush();
        window.Close();
    }

    [Fact]
    public async Task Refresh_button_uses_image_choice_cancellation()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(RecoveryApp));
        await session.Dispatch(async () =>
        {
            var window = new RecoveryWindow(); window.Show();
            using var stop = new CancellationTokenSource();
            var loads = new List<CancellationToken>();
            var pending = new TaskCompletionSource<LinuxRecoveryImageCatalog.Result>();
            window.ViewModel.CatalogLoader = token =>
            {
                loads.Add(token);
                return loads.Count == 1
                    ? Task.FromResult(new LinuxRecoveryImageCatalog.Result(LinuxRecoveryImageCatalog.State.Available, []))
                    : pending.Task;
            };
            var choice = window.ViewModel.ChooseImageFolderAsync(Guid.NewGuid(), stop.Token);
            await Flush();
            Click(window, "RefreshImages");
            Assert.Equal(2, loads.Count);
            stop.Cancel();
            Assert.True(loads[1].IsCancellationRequested);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await choice);
            pending.SetResult(new(LinuxRecoveryImageCatalog.State.Available, []));
            await Flush();
            await Finish(window, LinuxRecoveryControllerClient.Result.Cancelled);
            return 0;
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData("XID", 2L, true)]
    [InlineData("XID", 4294967295L, true)]
    [InlineData("XID", 0L, false)]
    [InlineData("XID", 1L, false)]
    [InlineData("XID", -1L, false)]
    [InlineData("XID", 4294967296L, false)]
    [InlineData("XID", long.MaxValue, false)]
    [InlineData("X11Window", 2L, false)]
    [InlineData("HWND", 2L, false)]
    [InlineData("Xid", 2L, false)]
    [InlineData("", 2L, false)]
    public void Native_focus_accepts_pinned_XID_handles_and_rejects_foreign_or_reserved_values(
        string descriptor, long value, bool expected)
    {
        var handle = new PlatformHandle((nint)value, descriptor);
        Assert.Equal(expected, RecoveryX11Focus.TryGetWindowId(handle, out nint windowId));
        Assert.Equal(expected ? (nint)value : 0, windowId);
    }

    [Fact]
    public void Missing_native_handle_cannot_become_a_focus_target()
    {
        Assert.False(RecoveryX11Focus.TryGetWindowId(null, out nint windowId));
        Assert.Equal(0, windowId);
    }

    [Fact]
    public async Task Keyboard_only_folder_choice_can_traverse_type_and_continue()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(RecoveryApp));
        await session.Dispatch(async () =>
        {
            var window = new RecoveryWindow(); window.Show();
            var pending = window.ViewModel.ChooseImageFolderAsync(Guid.NewGuid(), CancellationToken.None);
            await Flush();
            var language = window.FindControl<ComboBox>("Language")!;
            var folder = window.FindControl<TextBox>("Folder")!;
            var primary = window.FindControl<Button>("Primary")!;
            window.FindControl<Expander>("AdvancedFolder")!.IsExpanded = true;
            Assert.True(language.Focus());
            for (int index = 0; index < 10 && window.FocusManager!.GetFocusedElement() != folder; index++)
            {
                window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
                window.KeyReleaseQwerty(PhysicalKey.Tab, RawInputModifiers.None);
                await Flush();
            }
            Assert.Same(folder, window.FocusManager!.GetFocusedElement());
            window.KeyTextInput("/media/keyboard-image");
            window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Tab, RawInputModifiers.None);
            await Flush();
            Assert.Same(primary, window.FocusManager.GetFocusedElement());
            window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
            Assert.Equal("/media/keyboard-image", await pending.WaitAsync(TimeSpan.FromSeconds(2)));
            await Finish(window, LinuxRecoveryControllerClient.Result.Cancelled);
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Choices_preserve_target_and_format_when_language_changes()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(RecoveryApp));
        await session.Dispatch(async () =>
        {
            var window = new RecoveryWindow(); window.Show();
            var vm = window.ViewModel;
            var folder = vm.ChooseImageFolderAsync(Guid.NewGuid(), CancellationToken.None);
            await Flush();
            window.FindControl<TextBox>("Folder")!.Text = "/media/image";
            Click(window, "Primary");
            Assert.Equal("/media/image", await folder);
            var device = Device(); var inventory = Inventory(device);
            var target = vm.ChooseTargetAsync(Guid.NewGuid(), inventory, CancellationToken.None);
            await Flush();
            var list = window.FindControl<ListBox>("Targets")!;
            list.SelectedIndex = 0;
            var selected = list.SelectedItem;
            Assert.NotNull(selected);
            Assert.Contains(device.Capacity.ToString("N0", CultureInfo.GetCultureInfo("en")), Label(selected));
            var language = window.FindControl<ComboBox>("Language")!;
            language.SelectedItem = ((IEnumerable<LanguageOption>)language.ItemsSource!).Single(x => x.Code == "de");
            Assert.Same(selected, list.SelectedItem);
            Assert.False(target.IsCompleted);
            Assert.Contains(device.Capacity.ToString("N0", CultureInfo.GetCultureInfo("de")), Label(selected));
            Click(window, "Primary");
            Assert.Equal(device.KernelName, await target);
            await Finish(window, LinuxRecoveryControllerClient.Result.Cancelled);
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Close_requests_cancel_and_waits_for_runner_completion()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(RecoveryApp));
        await session.Dispatch(async () =>
        {
            var window = new RecoveryWindow(); window.Show();
            var vm = window.ViewModel; var token = vm.CancellationRequested;
            var pending = vm.ChooseImageFolderAsync(Guid.NewGuid(), CancellationToken.None);
            await Flush();
            window.Close();
            Assert.True(window.IsVisible);
            Assert.True(token.IsCancellationRequested);
            Assert.Null(await pending);
            Assert.False(vm.IsComplete);
            await Finish(window, LinuxRecoveryControllerClient.Result.Cancelled);
            Assert.False(window.IsVisible);
            Assert.True(vm.CancellationRequested.IsCancellationRequested);
            vm.Cancel(); // Safe after completion and source disposal.
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Choice_token_cancels_pending_prompt_without_closing_window()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(RecoveryApp));
        await session.Dispatch(async () =>
        {
            var window = new RecoveryWindow(); window.Show();
            using var stop = new CancellationTokenSource();
            var pending = window.ViewModel.ChooseImageFolderAsync(Guid.NewGuid(), stop.Token);
            await Flush();
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
            Assert.True(window.IsVisible);
            await Finish(window, LinuxRecoveryControllerClient.Result.Cancelled);
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Terminal_observation_supersedes_review_and_late_decisions()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(RecoveryApp));
        await session.Dispatch(async () =>
        {
            var window = new RecoveryWindow(); window.Show(); var vm = window.ViewModel;
            var device = Device(); var inventory = Inventory(device); var review = Review(device, inventory);
            var pending = vm.AcknowledgeReviewAsync(review, CancellationToken.None);
            await Flush();
            vm.Observe(new(LinuxRecoverySessionPhase.Refused, false, inventory, review, 0, null, LinuxRawRestoreOperationReason.ReviewRefused));
            await Flush();
            Assert.False(await pending);
            var late = vm.ChooseTargetAsync(review.OperationId, inventory, CancellationToken.None);
            await Flush();
            Assert.Null(await late);
            await Finish(window, LinuxRecoveryControllerClient.Result.Refused);
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Acknowledgment_and_progress_do_not_claim_verification_before_outcome()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(RecoveryApp));
        await session.Dispatch(async () =>
        {
            var window = new RecoveryWindow(); window.Show(); var vm = window.ViewModel;
            var device = Device(); var inventory = Inventory(device); var review = Review(device, inventory);
            var pending = vm.AcknowledgeReviewAsync(review, CancellationToken.None);
            await Flush();
            Assert.Contains(review.ManifestRoot, window.FindControl<TextBlock>("Details")!.Text);
            Click(window, "Primary");
            Assert.True(await pending);
            Assert.Contains("Ctrl+Alt+F1", window.FindControl<TextBlock>("Description")!.Text);
            vm.Observe(new(LinuxRecoverySessionPhase.Executing, false, inventory, review, 512, null, null));
            await Flush();
            Assert.Contains("512", window.FindControl<TextBlock>("Details")!.Text);
            Assert.DoesNotContain("read back", window.FindControl<TextBlock>("Description")!.Text!, StringComparison.OrdinalIgnoreCase);
            vm.Complete(LinuxRecoveryControllerClient.Result.Restored);
            await Flush();
            Assert.Contains("read back", window.FindControl<TextBlock>("Description")!.Text!, StringComparison.OrdinalIgnoreCase);
            window.Close();
            return 0;
        }, CancellationToken.None);
    }
    [Fact]
    public async Task Catalog_selection_needs_no_typed_path_and_survives_language_change()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(RecoveryApp));
        await session.Dispatch(async () =>
        {
            var window = new RecoveryWindow(); window.Show();
            string name = Guid.NewGuid().ToString("N");
            window.ViewModel.CatalogLoader = _ => Task.FromResult(new LinuxRecoveryImageCatalog.Result(
                LinuxRecoveryImageCatalog.State.Available, [new(name, 1234432)]));
            var choice = window.ViewModel.ChooseImageFolderAsync(Guid.NewGuid(), CancellationToken.None);
            await Flush();
            var list = window.FindControl<ListBox>("Images")!;
            list.SelectedIndex = 0;
            var selected = list.SelectedItem;
            Assert.NotNull(selected);
            Assert.Contains(name[..8], Label(selected));
            Assert.DoesNotContain("/srv/", Label(selected));
            window.FindControl<TextBox>("Folder")!.Text = "/media/manual-image";
            Assert.Null(list.SelectedItem);
            list.SelectedIndex = 0;
            Assert.Empty(window.FindControl<TextBox>("Folder")!.Text!);
            var language = window.FindControl<ComboBox>("Language")!;
            language.SelectedItem = ((IEnumerable<LanguageOption>)language.ItemsSource!).Single(x => x.Code == "de");
            Assert.Same(selected, list.SelectedItem);
            Assert.Contains(1234432UL.ToString("N0", CultureInfo.GetCultureInfo("de")), Label(selected));
            Assert.False(choice.IsCompleted);
            Assert.Empty(window.FindControl<TextBox>("Folder")!.Text!);
            Click(window, "Primary");
            Assert.Equal(LinuxRecoveryCaptureExport.Root + "/" + name, await choice);
            await Finish(window, LinuxRecoveryControllerClient.Result.Cancelled);
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Late_refresh_preserves_manual_input_and_cannot_replace_cancelled_screen()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(RecoveryApp));
        await session.Dispatch(async () =>
        {
            var window = new RecoveryWindow(); window.Show();
            var first = new TaskCompletionSource<LinuxRecoveryImageCatalog.Result>();
            var second = new TaskCompletionSource<LinuxRecoveryImageCatalog.Result>();
            window.ViewModel.CatalogLoader = _ => first.Task;
            var choice = window.ViewModel.ChooseImageFolderAsync(Guid.NewGuid(), CancellationToken.None);
            await Flush();
            var folder = window.FindControl<TextBox>("Folder")!;
            folder.Text = "/media/known-image";
            first.SetResult(new(LinuxRecoveryImageCatalog.State.Available, [new(Guid.NewGuid().ToString("N"), 4096)]));
            await Flush();
            Assert.Equal("/media/known-image", folder.Text);
            Assert.Null(window.FindControl<ListBox>("Images")!.SelectedItem);
            window.ViewModel.CatalogLoader = _ => second.Task;
            var refresh = window.ViewModel.RefreshImagesAsync(CancellationToken.None);
            window.ViewModel.Cancel();
            Assert.Null(await choice);
            string? heading = window.FindControl<TextBlock>("Heading")!.Text;
            second.SetException(new System.IO.IOException("synthetic late refresh failure"));
            await refresh;
            await Flush();
            Assert.Equal(heading, window.FindControl<TextBlock>("Heading")!.Text);
            Assert.False(window.FindControl<ListBox>("Images")!.IsVisible);
            await Finish(window, LinuxRecoveryControllerClient.Result.Cancelled);
            return 0;
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData((int)LinuxRecoveryImageCatalog.State.Available, "No recovery copies")]
    [InlineData((int)LinuxRecoveryImageCatalog.State.Unavailable, "unavailable")]
    [InlineData((int)LinuxRecoveryImageCatalog.State.Failed, "Could not read")]
    public async Task Catalog_outcomes_retain_advanced_known_folder_fallback(int state, string expected)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(RecoveryApp));
        await session.Dispatch(async () =>
        {
            var window = new RecoveryWindow(); window.Show();
            window.ViewModel.CatalogLoader = _ => Task.FromResult(new LinuxRecoveryImageCatalog.Result((LinuxRecoveryImageCatalog.State)state, []));
            var choice = window.ViewModel.ChooseImageFolderAsync(Guid.NewGuid(), CancellationToken.None);
            await Flush();
            Assert.Contains(expected, window.FindControl<TextBlock>("ImageStatus")!.Text);
            Assert.True(window.FindControl<Expander>("AdvancedFolder")!.IsVisible);
            window.FindControl<TextBox>("Folder")!.Text = "/media/known-image";
            Click(window, "Primary");
            Assert.Equal("/media/known-image", await choice);
            await Finish(window, LinuxRecoveryControllerClient.Result.Cancelled);
            return 0;
        }, CancellationToken.None);
    }

}
