# Oracle 备份管理系统

Windows 下使用 Oracle Data Pump 的计划备份和还原工具，包含 **Oracle备份端.exe** 和 **Oracle接受端.exe**。支持多任务、7z 高压缩、SHA-256 校验、TLS 传输以及按天保留备份。

## 安装

在 Releases 下载 `Oracle-Backup-Manager-v1.3.0-windows.zip` 并解压。两台服务器需要 .NET Framework 4.7.2 或更新版本和 TLS 1.2。Oracle 数据库服务器还需安装兼容的 `expdp.exe`、`impdp.exe` 和 `sqlplus.exe`。

1. 在备份存储服务器打开 **Oracle接受端.exe**，填写备份根目录和允许连接的备份端 IPv4 地址。多个地址用英文逗号分隔。放行这些地址到 TCP `17999` 的入站流量，再点击“启动接收”。
2. 在接受端点击“导出配对文件”，通过可信渠道将 `.obm-pair` 交给备份端。配对文件含连接凭据，请限制访问并在导入后妥善处理。
3. 在 Oracle 数据库实例所在的 Windows 服务器打开 **Oracle备份端.exe**。填写接受端 IP 或主机名，点击“导入配对”，选择上一步的文件。
4. 在备份端填写实际数据库地址、服务名、账号、密码和待备份的模式。填写 “Oracle bin” 或把 Oracle 工具目录加入 PATH。保存任务后，点击“备份当前任务”试跑。

通用版首次运行时连接字段为空。接受端在本机生成独立的 TLS 证书和连接密钥，配对信息和任务密码由当前 Windows 用户的 DPAPI 保护。两端使用此版本重新配对。设置保存在各自的 `%LOCALAPPDATA%\OracleBackupManager.Standard`。

## 日常使用

- **暂存目录**可留空。程序查询 Oracle 目录对象登记的本机路径。对象不可见时，程序会尝试建立暂存目录并创建 Oracle 目录对象。创建需要 `CREATE ANY DIRECTORY`。已有对象应由 DBA 核实映射并授予读写权限。若出现 `ORA-39070` 或 `ORA-29283`，检查 Oracle 数据库服务账号对实际暂存文件夹的 Windows 权限。
- **定时备份**可为每个任务设定间隔或每天的本地时间。备份端保持运行，到期任务依次执行。多个备份端共用接受端时，请错开计划。
- **压缩与保留**默认尝试 7z 高压缩，接受端核对原始文件大小和 SHA-256 后保存压缩包。压缩没有收益时发送 DMP。接受端可以按天自动清理。若要清理数据库服务器上的导出文件，可启用“接收并校验后删除本机导出文件”。
- **还原**时选择目标数据库的任务，刷新备份列表。选择备份后填写空目标账号和导入管理员。程序先下载和校验，再执行导入。成功后保留导入日志并清理临时 DMP。接受端可读取旧版同目录下的 ZIP 和 DMP 备份。

## 构建

在 64 位 Windows 上运行 `powershell -File .\build.ps1`。程序输出到 `build\app`，模拟 Oracle 和 TLS 测试结果放在 `build\test`。只构建 EXE 时可传入 `-BuildOnly`。`vendor\7zr.exe` 为 Igor Pavlov 的 LZMA SDK 26.06 公有领域组件。
