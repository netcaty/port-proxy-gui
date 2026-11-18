using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PortProxyManager.WinUI.ViewModels;

namespace PortProxyManager.WinUI.Views;

/// <summary>
/// 诊断页（内嵌在主窗口，不再单独开窗）。
/// 修复按钮通过 ViewModel.CloseRequested 把动作交回 MainViewModel；
/// 主窗口执行修复后会重新打开本页复查（旧版复查循环）。
/// </summary>
public sealed partial class DiagnosticsView : UserControl
{
    public DiagnosticsViewModel ViewModel { get; }

    public DiagnosticsView(DiagnosticsViewModel viewModel)
    {
        // x:Bind 在 InitializeComponent 期间求值，必须先赋值
        ViewModel = viewModel;

        InitializeComponent();

        Root.DataContext = ViewModel;

        Loaded += (_, _) => _ = ViewModel.RunDiagnosticsAsync();
        Unloaded += (_, _) => ViewModel.Cancel();
    }
}
