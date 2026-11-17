# PlatformIO 构建前脚本:注入 -fmacro-prefix-map 剥离构建机绝对路径。
# 直接写在 platformio.ini 的 build_flags 里不生效——${platformio.packages_dir}
# 展开为反斜杠路径,而 __FILE__ 里是正斜杠,映射键对不上(实测教训)。
Import("env")

platform = env.PioPlatform()
# 兼容不同 pio 版本:packages_dir 属性或 get_package_dir() 方法
pkg_dir = None
try:
    pkg_dir = str(platform.packages_dir)
except AttributeError:
    fw = platform.get_package("framework-arduinoespressif32")
    if fw is not None:
        pkg_dir = str(fw.path)
# 都取不到则回退到项目级 .platformio 常规位置(仅提示,不硬编码用户名)
if pkg_dir is None:
    pkg_dir = env.subst("$PROJECT_PACKAGES_DIR") or env.subst("${sysenv.USERPROFILE}/.platformio/packages")
pkg_dir = pkg_dir.replace("\\", "/")
env.Append(BUILD_FLAGS=[
    f"-fmacro-prefix-map={pkg_dir}/=pio/",
    f"-ffile-prefix-map={pkg_dir}/=pio/",
])
