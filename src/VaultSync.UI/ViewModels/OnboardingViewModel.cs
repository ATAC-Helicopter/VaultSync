using System;
using System.Collections.ObjectModel;
using System.Windows.Input;
using VaultSync.UI.Infrastructure;
using VaultSync.UI.Services;

namespace VaultSync.UI.ViewModels;

public sealed class OnboardingStep
{
    public int Number { get; }
    public string Text { get; }

    public OnboardingStep(int number, string text)
    {
        Number = number;
        Text = text;
    }
}

public sealed class OnboardingViewModel : ViewModelBase
{
    public string Title { get; }
    public string Subtitle { get; }
    public ObservableCollection<OnboardingStep> Steps { get; } = [];

    public ICommand OpenProjectsCommand { get; }
    public ICommand CloseCommand { get; }

    public event Action? OpenProjectsRequested;
    public event Action? CloseRequested;

    public OnboardingViewModel()
    {
        Title = L("Onboarding.Title", "Getting started");
        Subtitle = L("Onboarding.Subtitle", "A quick setup path to your first backup.");
        Steps.Add(new OnboardingStep(1, L("Onboarding.Step1", "Add your projects root and register a project.")));
        Steps.Add(new OnboardingStep(2, L("Onboarding.Step2", "Pick a preset (auto-selected) or choose No preset.")));
        Steps.Add(new OnboardingStep(3, L("Onboarding.Step3", "Run your first snapshot or backup.")));

        OpenProjectsCommand = new RelayCommand(_ => OpenProjectsRequested?.Invoke());
        CloseCommand = new RelayCommand(_ => CloseRequested?.Invoke());
    }

    private static string L(string key, string fallback) =>
        LocalizationProvider.Service?.GetString(key) ?? fallback;
}
