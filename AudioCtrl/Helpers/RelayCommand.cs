using System;
using System.Windows.Input;

namespace AudioCtrl.Helpers;

/// <summary>
/// 轻量命令实现，用于 MVVM 模式中的 ICommand 接口
/// 将按钮点击等 UI 事件绑定到 ViewModel 的方法上
/// </summary>
public class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    /// <summary>
    /// 创建命令，传入执行逻辑和可选的可执行条件
    /// </summary>
    /// <param name="execute">命令执行时的回调</param>
    /// <param name="canExecute">命令是否可执行（null 表示始终可执行）</param>
    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    /// <summary>
    /// 判断命令是否可执行，WPF 绑定系统会调用此方法
    /// </summary>
    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    /// <summary>
    /// 执行命令，WPF 绑定系统在用户交互时会调用此方法
    /// </summary>
    public void Execute(object? parameter) => _execute(parameter);

    /// <summary>
    /// 命令可执行状态变化事件，通知 WPF 重新查询 CanExecute
    /// </summary>
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
}
