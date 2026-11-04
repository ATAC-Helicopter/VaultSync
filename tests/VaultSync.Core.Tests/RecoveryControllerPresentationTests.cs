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
using Avalonia.Threading;
using VaultSync.Core.Models;
using VaultSync.Core.Services;
using RecoveryApp = RecoveryController::VaultSync.RecoveryController.RecoveryApp;
using RecoveryWindow = RecoveryController::VaultSync.RecoveryController.RecoveryWindow;
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
}
