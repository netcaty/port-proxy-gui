using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PortProxyManager.WinUI.ViewModels;

namespace PortProxyManager.WinUI.Views;

/// <summary>
/// 新增/编辑映射表单。内嵌在主窗口里显示（不再是独立弹窗），
/// 结果经 <see cref="EntryEditViewModel.Confirmed"/> / Result 回传，
/// 关闭动作由 MainViewModel 订阅 CloseRequested 后处理。
/// </summary>
public sealed partial class EntryEditView : UserControl
{
    public EntryEditViewModel ViewModel { get; }

    public EntryEditView(EntryEditViewModel viewModel)
    {
        // x:Bind 在 InitializeComponent 期间求值，必须先赋值
        ViewModel = viewModel;

        InitializeComponent();

        Root.DataContext = ViewModel;

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Unloaded += (_, _) => ViewModel.PropertyChanged -= OnViewModelPropertyChanged;

        // 视图进入可视树后再填一次候选：让目标框在绑定就绪后显示当前地址
        Loaded += (_, _) =>
        {
            ViewModel.PopulateCandidates();
            ConnectBox.Text = ViewModel.ConnectText;
        };
    }

    /// <summary>
    /// 可编辑 ComboBox 的 Text 在候选集合重建后会被内部文本框清掉，
    /// 这里跟随 ViewModel.ConnectText 手动同步显示。
    /// </summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EntryEditViewModel.ConnectText))
            ConnectBox.Text = ViewModel.ConnectText;
    }

    /// <summary>候选下拉选中：手动填写项清空文本，其余保留展示文本。</summary>
    private void OnConnectSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ViewModel.CandidateCommitted(ConnectBox.SelectedItem as CandidateRow);
    }

    /// <summary>手输提交（Enter）：清洗后更新。</summary>
    private void OnConnectTextSubmitted(ComboBox sender, ComboBoxTextSubmittedEventArgs args)
    {
        ViewModel.SubmitConnectText(args.Text);
    }

    /// <summary>失焦也提交：用户手输后直接点"创建"的路径不能丢文本。</summary>
    private void OnConnectLostFocus(object sender, RoutedEventArgs e)
    {
        ViewModel.SubmitConnectText(ConnectBox.Text);
    }
}
