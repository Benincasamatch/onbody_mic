use std::io::Write;
use std::path::{Path, PathBuf};
use std::process::Command;
use std::sync::Arc;
use std::time::{Duration, Instant};

use serde::Serialize;
use sha2::{Digest, Sha256};
use tauri::{AppHandle, Emitter};

use crate::models::{AppResult, CommandError};
use crate::runtime::DesktopRuntime;

pub const UPDATE_EVENT: &str = "update_progress";
const RELEASE_PREFIX: &str = "https://github.com/Benincasamatch/onbody_mic/releases/download/";
const HELPER_SCRIPT: &str = include_str!("update-helper.ps1");

/// How this copy was delivered: NSIS install (updates itself through the signed
/// installer) or a portable folder (updates itself by replacing `win-x64`).
pub fn install_kind() -> &'static str {
    if cfg!(debug_assertions) {
        return "development";
    }
    let Some(directory) = std::env::current_exe().ok().and_then(|exe| exe.parent().map(Path::to_path_buf)) else {
        return "unknown";
    };
    if directory.join("uninstall.exe").is_file() { "installed" } else { "portable" }
}

#[derive(Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct UpdateProgress {
    phase: &'static str,
    received: u64,
    total: Option<u64>,
}

fn emit_progress(app: &AppHandle, phase: &'static str, received: u64, total: Option<u64>) {
    let _ = app.emit(UPDATE_EVENT, UpdateProgress { phase, received, total });
}

fn command_error(code: &'static str, message: impl Into<String>) -> CommandError {
    CommandError::new(code, message.into())
}

fn update_directory() -> AppResult<PathBuf> {
    let directory = std::env::temp_dir().join("onbodymic-update");
    std::fs::create_dir_all(&directory).map_err(|error| command_error("io-error", format!("无法创建更新临时目录: {error}")))?;
    Ok(directory)
}

fn normalized_hash(value: &str) -> AppResult<String> {
    let hash = value.trim().to_ascii_lowercase();
    if hash.len() != 64 || !hash.bytes().all(|byte| byte.is_ascii_hexdigit()) {
        return Err(command_error("invalid-argument", "更新包校验值格式错误。"));
    }
    Ok(hash)
}

fn client() -> AppResult<reqwest::Client> {
    if rustls::crypto::CryptoProvider::get_default().is_none() {
        // Same provider the updater plugin installs before its own requests.
        let _ = rustls::crypto::ring::default_provider().install_default();
    }
    reqwest::Client::builder()
        .user_agent(concat!("OnbodyMic/", env!("CARGO_PKG_VERSION")))
        .connect_timeout(Duration::from_secs(15))
        .timeout(Duration::from_secs(900))
        .build()
        .map_err(|error| command_error("network-error", format!("无法初始化下载客户端: {error}")))
}

fn validate_url(url: &str) -> AppResult<()> {
    let loopback = std::env::var("ONBODYMIC_UPDATE_ALLOW_LOCAL").is_ok_and(|value| value == "1")
        && url.starts_with("http://127.0.0.1:");
    if (url.starts_with(RELEASE_PREFIX) || loopback) && !url.contains(|character: char| character.is_whitespace()) {
        Ok(())
    } else {
        Err(command_error("invalid-argument", "更新包地址不属于本项目的发布资源。"))
    }
}

async fn download_zip(app: &AppHandle, url: &str, expected_size: Option<u64>) -> AppResult<(PathBuf, String)> {
    validate_url(url)?;
    let directory = update_directory()?;
    let path = directory.join("portable.zip");
    let _ = std::fs::remove_file(&path);
    let response = client()?.get(url).send().await.map_err(|error| command_error("network-error", format!("下载更新包失败: {error}")))?;
    if !response.status().is_success() {
        return Err(command_error("network-error", format!("更新包下载返回 HTTP {}。", response.status().as_u16())));
    }
    let total = expected_size.or_else(|| response.content_length());
    let mut file = std::fs::File::create(&path).map_err(|error| command_error("io-error", format!("无法写入更新包: {error}")))?;
    let mut response = response;
    let mut hasher = Sha256::new();
    let mut received = 0_u64;
    let mut last_emit = Instant::now();
    emit_progress(app, "downloading", 0, total);
    loop {
        let chunk = response.chunk().await.map_err(|error| command_error("network-error", format!("下载中断: {error}")))?;
        let Some(chunk) = chunk else { break };
        file.write_all(&chunk).map_err(|error| command_error("io-error", format!("写入更新包失败: {error}")))?;
        hasher.update(&chunk);
        received += chunk.len() as u64;
        if last_emit.elapsed() >= Duration::from_millis(200) {
            last_emit = Instant::now();
            emit_progress(app, "downloading", received, total);
        }
    }
    file.flush().map_err(|error| command_error("io-error", format!("写入更新包失败: {error}")))?;
    drop(file);
    emit_progress(app, "downloading", received, total);
    if let Some(expected) = expected_size {
        if received != expected {
            let _ = std::fs::remove_file(&path);
            return Err(command_error("invalid-data", format!("更新包大小不符：收到 {received} 字节，应为 {expected} 字节。")));
        }
    }
    let digest = hasher.finalize().iter().map(|byte| format!("{byte:02x}")).collect::<String>();
    Ok((path, digest))
}

fn write_helper(directory: &Path) -> AppResult<PathBuf> {
    let path = directory.join("apply-update.ps1");
    std::fs::write(&path, HELPER_SCRIPT).map_err(|error| command_error("io-error", format!("无法写入更新辅助脚本: {error}")))?;
    Ok(path)
}

fn quote_argument(argument: &str) -> String {
    if !argument.is_empty() && !argument.contains(|character: char| character == ' ' || character == '\t' || character == '"') {
        return argument.to_owned();
    }
    let mut quoted = String::from("\"");
    let mut backslashes = 0_usize;
    for character in argument.chars() {
        match character {
            '\\' => { backslashes += 1; quoted.push('\\'); }
            '"' => {
                quoted.push_str(&"\\".repeat(backslashes + 1));
                quoted.push('"');
                backslashes = 0;
            }
            _ => { backslashes = 0; quoted.push(character); }
        }
    }
    quoted.push_str(&"\\".repeat(backslashes));
    quoted.push('"');
    quoted
}

fn write_restart_args(directory: &Path) -> AppResult<PathBuf> {
    let path = directory.join("restart-args.txt");
    let arguments = std::env::args().skip(1).map(|argument| quote_argument(&argument)).collect::<Vec<_>>().join(" ");
    std::fs::write(&path, arguments).map_err(|error| command_error("io-error", format!("无法写入重启参数: {error}")))?;
    Ok(path)
}

fn powershell_path() -> PathBuf {
    if let Some(root) = std::env::var_os("SystemRoot") {
        let candidate = Path::new(&root).join("System32").join("WindowsPowerShell").join("v1.0").join("powershell.exe");
        if candidate.is_file() {
            return candidate;
        }
    }
    PathBuf::from("powershell.exe")
}

fn spawn_helper(app: &AppHandle, helper: &Path, zip: &Path, app_dir: &Path, exe: &Path, args_file: &Path) -> AppResult<()> {
    use tauri_plugin_shell::ShellExt;
    let program = powershell_path();
    let arguments = ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File"]
        .map(str::to_owned)
        .into_iter()
        .chain([
            helper.to_string_lossy().into_owned(),
            "-AppPid".to_owned(), std::process::id().to_string(),
            "-Zip".to_owned(), zip.to_string_lossy().into_owned(),
            "-AppDir".to_owned(), app_dir.to_string_lossy().into_owned(),
            "-Exe".to_owned(), exe.to_string_lossy().into_owned(),
            "-ArgsFile".to_owned(), args_file.to_string_lossy().into_owned(),
        ]);
    // The shell plugin spawns the same way this app already launches the audio
    // backend (CREATE_NO_WINDOW + piped stdio), which works in packaged builds
    // where a plain std spawn was rejected with ERROR_NOT_SUPPORTED.
    match app.shell().command(&program).args(arguments.clone()).spawn() {
        Ok((_events, _child)) => return Ok(()),
        Err(plugin_error) => {
            let mut command = Command::new(&program);
            command.args(arguments);
            #[cfg(windows)]
            {
                use std::os::windows::process::CommandExt;
                command.creation_flags(0x0800_0000); // CREATE_NO_WINDOW
            }
            command.spawn().map(|_| ()).map_err(|error| command_error("io-error", format!("无法启动更新辅助程序: {error}（插件: {plugin_error}）")))
        }
    }
}

/// Downloads the signed portable archive, verifies its SHA-256, then hands the
/// file swap to a helper that waits for this process to exit and restarts it.
#[tauri::command]
pub async fn install_portable_update(
    app: AppHandle,
    runtime: tauri::State<'_, Arc<DesktopRuntime>>,
    url: String,
    sha256: String,
    size: Option<u64>,
) -> AppResult<bool> {
    if install_kind() != "portable" {
        return Err(command_error("invalid-state", "当前版本不是便携版，无法使用便携包更新。"));
    }
    let expected = normalized_hash(&sha256)?;
    let (zip, digest) = download_zip(&app, &url, size).await?;
    if digest != expected {
        let _ = std::fs::remove_file(&zip);
        return Err(command_error("invalid-data", "更新包校验失败，已放弃安装。请重新下载。"));
    }
    let exe = std::env::current_exe().map_err(|error| command_error("io-error", format!("无法定位程序位置: {error}")))?;
    let app_dir = exe.parent().ok_or_else(|| command_error("io-error", "无法定位程序目录。"))?.to_path_buf();
    let helper = write_helper(&zip.parent().expect("update directory has a parent"))?;
    let args_file = write_restart_args(&zip.parent().expect("update directory has a parent"))?;
    spawn_helper(&app, &helper, &zip, &app_dir, &exe, &args_file)?;
    emit_progress(&app, "installing", 0, None);
    runtime.exit_for_update().await;
    app.exit(0);
    Ok(true)
}

/// Verifies an already downloaded archive against a manifest hash; used by tests.
#[cfg(test)]
pub fn verify_archive(path: &Path, expected: &str) -> AppResult<()> {
    use std::io::Read;
    let expected = normalized_hash(expected)?;
    let mut file = std::fs::File::open(path).map_err(|error| command_error("io-error", format!("无法读取更新包: {error}")))?;
    let mut hasher = Sha256::new();
    let mut buffer = [0_u8; 64 * 1024];
    loop {
        let read = file.read(&mut buffer).map_err(|error| command_error("io-error", format!("无法读取更新包: {error}")))?;
        if read == 0 { break }
        hasher.update(&buffer[..read]);
    }
    let actual = hasher.finalize().iter().map(|byte| format!("{byte:02x}")).collect::<String>();
    if actual != expected {
        return Err(command_error("invalid-data", "更新包校验失败"));
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn normalizes_hashes_and_rejects_malformed_values() {
        let hex = "a".repeat(64);
        assert_eq!(normalized_hash(&hex).unwrap(), hex);
        assert_eq!(normalized_hash(&"A".repeat(64)).unwrap(), hex);
        assert!(normalized_hash("  ").is_err());
        assert!(normalized_hash(&"g".repeat(64)).is_err());
        assert!(normalized_hash(&"a".repeat(63)).is_err());
        assert!(normalized_hash(&"a".repeat(65)).is_err());
    }

    #[test]
    fn only_accepts_release_download_urls_of_this_repository() {
        assert!(validate_url("https://github.com/Benincasamatch/onbody_mic/releases/download/v0.8.0/OnbodyMic-0.8.0-desktop-portable-x64.zip").is_ok());
        assert!(validate_url("https://github.com/other/onbody_mic/releases/download/v0.8.0/a.zip").is_err());
        assert!(validate_url("https://github.com/Benincasamatch/onbody_mic/releases/download-evil/a.zip").is_err());
        assert!(validate_url("http://github.com/Benincasamatch/onbody_mic/releases/download/a.zip").is_err());
        assert!(validate_url("http://127.0.0.1:8123/OnbodyMic.zip").is_err());
        assert!(validate_url("https://github.com/Benincasamatch/onbody_mic/releases/download/a b.zip").is_err());
    }

    #[test]
    fn quotes_restart_arguments_for_windows() {
        assert_eq!(quote_argument("--root"), "--root");
        assert_eq!(quote_argument(r"C:\data root"), "\"C:\\data root\"");
        assert_eq!(quote_argument("C:\\dir\\"), "C:\\dir\\");
        assert_eq!(quote_argument("C:\\my dir\\"), "\"C:\\my dir\\\\\"");
        assert_eq!(quote_argument("a\"b"), "\"a\\\"b\"");
        assert_eq!(quote_argument(""), "\"\"");
    }

    #[test]
    fn verifies_archive_hashes() {
        let directory = tempfile::tempdir().unwrap();
        let path = directory.path().join("payload.zip");
        std::fs::write(&path, b"onbody").unwrap();
        let digest = sha2::Sha256::digest(b"onbody").iter().map(|byte| format!("{byte:02x}")).collect::<String>();
        assert!(verify_archive(&path, &digest).is_ok());
        let mut tampered = digest.clone();
        tampered.replace_range(0..1, if digest.starts_with('0') { "1" } else { "0" });
        assert!(verify_archive(&path, &tampered).is_err());
    }
}