using AudioCtrl.Models;

namespace AudioCtrl.Services;

/// <summary>
/// 音频设备服务的抽象面(供调度器使用)。
/// 生产实现是 <see cref="AudioDeviceService"/>;测试可注入轻量 fake,
/// 避免在单元测试里创建真实 COM/Core Audio 对象。
/// </summary>
public interface IAudioDeviceService
{
    /// <summary>枚举所有播放/录音设备(含未连接端点)。</summary>
    List<AudioDeviceInfo> EnumerateDevices();

    /// <summary>把指定设备设为系统默认设备(播放/录制)。</summary>
    bool SetDefaultDevice(string deviceId, string type);

    /// <summary>
    /// 指定设备是否同时是 Console / Multimedia / Communications 三个角色的默认设备。
    /// 调度器的“已是默认则跳过”必须以三角色一致为准:只查 Multimedia 会在
    /// 用户按角色分离默认设备时漏掉 Console/Communications 的对齐。
    /// </summary>
    bool IsDefaultForAllRoles(string deviceId, string type);
}
