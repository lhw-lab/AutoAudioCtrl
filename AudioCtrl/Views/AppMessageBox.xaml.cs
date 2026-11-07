using System.Windows;
using AudioCtrl.Helpers;

namespace AudioCtrl.Views;

/// <summary>
/// 应用风格统一弹窗（替代原生 MessageBox，主题跟随深浅色）。
/// ESC = 否/关闭；Enter = 是/确定。
/// </summary>
public partial class AppMessageBox : Window
{
    /// <summary>结果：true=是/确定，false=否/关闭</summary>
    private bool _result;

    private AppMessageBox(string title, string message, bool yesNo)
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;
        YesButton.Content = Loc.T(yesNo ? "S.Common.Yes" : "S.Common.Ok");
        NoButton.Visibility = yesNo ? Visibility.Visible : Visibility.Collapsed;
        // OK 模式下主按钮居右独占，无需隐藏导致的布局偏移
        if (!yesNo) NoButton.Margin = new Thickness(0);
    }

    /// <summary>
    /// 显示弹窗并等待用户选择。
    /// </summary>
    /// <param name="owner">宿主窗口（可空：单实例提示时主窗口尚未创建）</param>
    /// <param name="title">标题</param>
    /// <param name="message">正文</param>
    /// <param name="yesNo">true=是/否两键，false=仅确定</param>
    /// <returns>true=是/确定；false=否或直接关闭</returns>
    public static bool Show(Window? owner, string title, string message, bool yesNo = false)
    {
        var box = new AppMessageBox(title, message, yesNo);
        if (owner != null)
        {
            box.Owner = owner;
            box.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        box.ShowDialog();
        Logger.Info($"[弹窗] \"{title}\" -> {(box._result ? "是/确定" : "否/关闭")}");
        return box._result;
    }

    private void OnYesClick(object sender, RoutedEventArgs e)
    {
        _result = true;
        Close();
    }

    private void OnNoClick(object sender, RoutedEventArgs e)
    {
        _result = false;
        Close();
    }

    protected override void OnKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            _result = false;
            Close();
        }
        base.OnKeyDown(e);
    }

    /// <summary>点遮罩外区域不关闭（无边框窗口无处可点，保留标题拖动能力）</summary>
    protected override void OnMouseLeftButtonDown(System.Windows.Input.MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
            DragMove();
    }
}
