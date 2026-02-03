using Avalonia.Controls;
using VaultSync.UI.ViewModels;

namespace VaultSync.UI.Views;

public partial class OnboardingWindow : Window
{
    public OnboardingWindow()
    {
        InitializeComponent();

        DataContextChanged += (_, _) =>
        {
            if (DataContext is OnboardingViewModel vm)
            {
                vm.CloseRequested += () => Close();
                vm.OpenProjectsRequested += () => Close();
            }
        };
    }
}
