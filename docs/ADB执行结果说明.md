# ADB 执行结果与诊断

通用执行层使用真实 `Process.ExitCode`：0 代表命令正常结束，stderr 非空不代表失败。stdout/stderr 并行完整读取并分别记录；`Binder ioctl to enable oneway spam detection failed: Invalid argument` 保留为 `ADB Warning:`。

真实非零退出码会保留，不能仅靠 `remount succeeded` 字符串将其改写为成功。启动失败、超时、取消和输出读取异常分别报告，不伪造 -1。安装步骤除退出码 0 外，还要求 stdout 中有独立 `Success` 行。

已有 `uid=0` 时不重复执行 `adb root`。状态检测和数据导出不会执行 root/remount；执行涉及系统库或 Launcher 的部署时，才按原流程获取必要权限。

此前检查过旧 EXE：没有“stderr 非空就返回 -1”的分支，旧退出码来自 `process.ExitCode`。已修正把 stdout/stderr 合并后与 `device` 比较而导致的在线状态误判。原个案的 -1 未在隔离模拟环境复现，因此没有将未知非零退出强行放行。

遇到异常请保留同次命令的 `ADB executable`、stdout、stderr 和 `ADB Process` 全部日志，以确认程序路径、输出来源和实际退出码。
