using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AudioCtrl.Helpers;

/// <summary>
/// ViewModel 基类，实现 INotifyPropertyChanged 接口
/// 当 ViewModel 属性值改变时通知 WPF 绑定系统更新 UI
/// </summary>
public abstract class ViewModelBase : INotifyPropertyChanged
{
    /// <summary>
    /// 属性变化事件，WPF 绑定系统监听此事件来更新界面
    /// </summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// 触发属性变化通知，调用后 WPF 会刷新对应绑定控件
    /// </summary>
    /// <param name="propertyName">属性名（调用处自动推断，无需手动传入）</param>
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    /// <summary>
    /// 设置属性值并在值变化时触发通知
    /// 避免无意义的赋值和通知
    /// </summary>
    /// <typeparam name="T">属性类型</typeparam>
    /// <param name="field">属性 backing 字段引用</param>
    /// <param name="value">新值</param>
    /// <param name="propertyName">属性名（自动推断）</param>
    /// <returns>值是否发生变化</returns>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
