# Oracle Backup Manager

[简体中文](README.md) · English

Schedule and restore Oracle schema backups on Windows with Oracle Data Pump. The application supports multiple backup jobs, 7z compression, SHA-256 verification, TLS transfers, and retention by age.

**Technology stack** C# · Windows Forms (WinForms) · .NET Framework

| Application | Where to run it | What it does |
| --- | --- | --- |
| `Oracle备份端.exe` (Backup Client) | Windows server hosting the Oracle database instance | Exports, sends, and restores backups |
| `Oracle接受端.exe` (Backup Receiver) | Backup storage server | Receives, verifies, and retains backups |

## Installation

Download the Windows ZIP from [GitHub Releases](https://github.com/sunweike/oracle-backup-manager/releases) and extract it. Both Windows servers need .NET Framework 4.7.2 or later and TLS 1.2 enabled. The database server also needs compatible versions of `expdp.exe`, `impdp.exe`, and `sqlplus.exe`.

The desktop interface is in Simplified Chinese. The steps below include the labels shown in the app.

1. Open **Backup Receiver** on the storage server. Enter the backup root folder (`备份根目录`) and the allowed client IPv4 addresses (`允许来源 IP`), separated by commas. Allow inbound TCP `17999` from those addresses in the firewall, then click **启动接收** (Start Receiver).
2. On the receiver, click **导出配对文件** (Export Pairing File). Send the `.obm-pair` file to the backup client over a trusted channel. The file contains a connection credential, so keep access restricted.
3. Open **Backup Client** on the database server. Enter the receiver's IP address or hostname in `接受端 IP`, then click **导入配对** (Import Pairing) and select the file.
4. Enter the database host, service name, account, password, and schema to back up. Set **Oracle bin** to the Oracle tools folder or add that folder to `PATH`. Click **保存任务** (Save Job), then **备份当前任务** (Run Selected Backup) to test the setup.

Connection fields start blank. The receiver generates its TLS certificate and connection token locally. Windows DPAPI protects stored pairing details and task passwords under the current Windows account. Both applications keep their settings under `%LOCALAPPDATA%\OracleBackupManager.Standard`. To upgrade from v1.3.0 or v1.4.0, replace both executables and run each under its original Windows account to keep saved jobs and pairing details. If pairing file export previously failed, click **导出配对文件** (Export Pairing File) again after the upgrade. To upgrade from an earlier version with built-in pairing credentials, replace both executables and pair them again.

Database passwords can contain ASCII punctuation such as `@`, `/`, `&`, `!`, and `#`. Enter the password exactly as it is. The application handles quoting for the Oracle tools. The password field accepts 1 to 128 printable ASCII characters, excluding double quotes. The database's own password policy still applies.

## Daily operation

- **Staging directory.** Leave the field blank to use the local path registered in the Oracle directory object. If the object cannot be found, the program attempts to create the staging folder and directory object. The database account needs `CREATE ANY DIRECTORY` to create the object. For existing objects, ask the DBA to verify the path and grant `READ` and `WRITE` access. If Data Pump reports `ORA-39070` or `ORA-29283`, check Windows permissions for the Oracle database service account on the staging folder.
- **Scheduling.** Set an hourly interval or a daily run time for each job. Daily schedules use the server's local time. Keep the backup client running so it can process due jobs in order. Stagger schedules when several clients share a receiver.
- **Compression level.** Select a job and choose No Compression, Fast (3), Balanced (5), or Maximum (9) in **7z 压缩等级**, then save the job. New jobs default to Fast. When upgrading, a previously enabled checkbox maps to Maximum, while a disabled checkbox maps to No Compression. Changes take effect the next time the job runs.
- **Progress and retention.** The client shows progress while hashing, compressing, sending, and waiting for receiver verification. If compression does not make a dump smaller, the client sends the DMP directly. The receiver verifies the original dump size and SHA-256 before keeping the archive. Set a retention period on the receiver to remove expired backups. Enable **接收并校验后删除本机导出文件** to remove the local export after the receiver confirms it.
- **Restoring a backup.** Select the job for the destination database and click **刷新备份列表** (Refresh Backups). Select a backup, enter an empty target schema and import administrator credentials, then click **还原到空账号** (Restore to Empty Schema). The client verifies the downloaded data before importing it. A successful import removes the temporary DMP and keeps the import log. The receiver can also read backups stored by earlier versions in ZIP and DMP formats.

## Build from source

Run the following command on 64-bit Windows.

```powershell
powershell -File .\build.ps1
```

The executables are written to `build\app`. Results from the simulated Oracle and TLS tests are written to `build\test`. Pass `-BuildOnly` to build the executables without running the tests.

The bundled `vendor\7zr.exe` comes from Igor Pavlov's public domain LZMA SDK 26.06. See the [third-party notice](vendor/NOTICE.txt).
