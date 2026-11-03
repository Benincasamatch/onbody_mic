use std::path::PathBuf;
use std::sync::{Arc, Mutex};
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::time::Duration;
use serde::Serialize;
use serde_json::{Map, Value, json};
use tauri::{AppHandle, Emitter, Manager, Wry};
use tauri::menu::{CheckMenuItem, Menu, MenuItem};
use tauri::tray::{MouseButton, MouseButtonState, TrayIcon, TrayIconBuilder, TrayIconEvent};
use tauri::window::{Effect, EffectsBuilder};
use crate::broker::EngineBroker;
use crate::models::{AppResult, CommandError, PresetConfig, PresetSummary, SettingsData, SettingsIssue};
use crate::store::Store;

#[derive(Clone, Serialize)]
pub struct EngineView {
    #[serde(flatten)] runtime: Arc<Value>,
    #[serde(flatten)] library: Arc<Value>,
    #[serde(flatten)] hotkeys: Arc<Value>,
}
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DesktopView {
    root: Arc<String>,
    settings: Arc<SettingsData>,
    settings_error: Option<Arc<CommandError>>,
    settings_issues: Arc<Vec<SettingsIssue>>,
    presets: Vec<PresetSummary>,
    active_preset: Option<PresetConfig>,
    engine: Option<EngineView>,
    engine_error: Option<Arc<String>>,
    version: &'static str,
}

struct TrayState {
    _icon: TrayIcon<Wry>,
    mic: CheckMenuItem<Wry>,
    extras: CheckMenuItem<Wry>,
    monitor: CheckMenuItem<Wry>,
    mute: MenuItem<Wry>,
}

pub struct DesktopRuntime {
    app: AppHandle,
    root: PathBuf,
    root_text: Arc<String>,
    store: Mutex<Store>,
    broker: Mutex<Option<Arc<EngineBroker>>>,
    engine: Mutex<Option<EngineView>>,
    engine_error: Mutex<Option<Arc<String>>>,
    pending_runtime: Mutex<Option<Value>>,
    generation: AtomicU64,
    revision: AtomicU64,
    dirty: AtomicBool,
    exiting: AtomicBool,
    startup: tokio::sync::Mutex<()>,
    tray: Mutex<Option<TrayState>>,
}

impl DesktopRuntime {
    pub fn new(app: AppHandle) -> AppResult<Self> {
        let mut arguments = std::env::args().skip(1);
        let mut supplied = None;
        while let Some(argument) = arguments.next() {
            if argument == "--root" {
                supplied = Some(PathBuf::from(arguments.next().ok_or_else(|| CommandError::new("invalid-argument", "--root 缺少数据目录"))?));
            }
        }
        let root = supplied.or_else(|| std::env::var_os("LOCALAPPDATA").map(|path| PathBuf::from(path).join("OnbodyMic")))
            .ok_or_else(|| CommandError::new("io-error", "无法确定本地数据目录"))?;
        let store = Store::open(root)?;
        let root = store.root().to_path_buf();
        let root_text = Arc::new(root.to_string_lossy().into_owned());
        Ok(Self { app, root, root_text, store: Mutex::new(store), broker: Mutex::new(None), engine: Mutex::new(None), engine_error: Mutex::new(None), pending_runtime: Mutex::new(None), generation: AtomicU64::new(0), revision: AtomicU64::new(0), dirty: AtomicBool::new(false), exiting: AtomicBool::new(false), startup: tokio::sync::Mutex::new(()), tray: Mutex::new(None) })
    }

    pub fn is_exiting(&self) -> bool { self.exiting.load(Ordering::Acquire) }
    pub fn data_root(&self) -> &std::path::Path { &self.root }

    pub async fn snapshot(&self) -> AppResult<DesktopView> {
        let engine = self.engine.lock().expect("engine state poisoned").clone();
        let store = self.store.lock().expect("configuration store poisoned");
        let selected = match engine.as_ref() {
            Some(engine) => engine.runtime.get("status").and_then(|status| status.get("lastPresetId")).and_then(Value::as_str),
            None => store.settings().last_preset_id.as_deref(),
        };
        let active_preset = selected.and_then(|id| store.load_preset(id).ok());
        Ok(DesktopView {
            root: self.root_text.clone(), settings: store.settings_shared(),
            settings_error: store.settings_error_shared(), settings_issues: store.settings_issues_shared(),
            presets: store.list_presets()?, active_preset, engine,
            engine_error: self.engine_error.lock().expect("engine error poisoned").clone(), version: env!("CARGO_PKG_VERSION"),
        })
    }

    pub async fn start_engine(&self) -> AppResult<()> {
        let _startup = self.startup.lock().await;
        let generation = self.generation.fetch_add(1, Ordering::AcqRel) + 1;
        if let Some(old) = self.broker.lock().expect("backend handle poisoned").take() { old.terminate(); }
        *self.engine.lock().expect("engine state poisoned") = None;
        *self.pending_runtime.lock().expect("pending state poisoned") = None;
        *self.engine_error.lock().expect("engine error poisoned") = None;
        let (settings, presets) = {
            let store = self.store.lock().expect("configuration store poisoned");
            let presets = store.list_presets()?.iter().filter(|summary| !summary.corrupt).map(|summary| store.load_preset(&summary.preset_id)).collect::<AppResult<Vec<_>>>()?;
            (store.settings_shared(), presets)
        };
        let broker = EngineBroker::spawn(&self.app, &self.root, generation)?;
        *self.broker.lock().expect("backend handle poisoned") = Some(broker.clone());
        let initialized = match broker.request("init", json!({"settings":settings,"presets":presets})).await {
            Ok(initialized) => initialized,
            Err(error) => { broker.terminate(); return Err(error); }
        };
        if generation != self.generation.load(Ordering::Acquire) { return Err(CommandError::new("backend-unavailable", "后端初始化请求已过期")); }
        let mut engine = split_engine_snapshot(initialized)?;
        if let Some(pending) = self.pending_runtime.lock().expect("pending state poisoned").take() {
            if pending.get("revision").and_then(Value::as_u64).unwrap_or(0) > engine.runtime.get("revision").and_then(Value::as_u64).unwrap_or(0) { engine.runtime = Arc::new(pending); }
        }
        let revision = self.revision.fetch_add(1, Ordering::AcqRel) + 1;
        Arc::make_mut(&mut engine.runtime)["revision"] = Value::from(revision);
        *self.engine.lock().expect("engine state poisoned") = Some(engine);
        self.refresh_tray();
        self.apply_transparency();
        self.emit_changed();
        if self.store.lock().expect("configuration store poisoned").settings().ui.start_minimized {
            if let Some(window) = self.app.get_webview_window("main") { window.hide().map_err(|error| CommandError::new("window-error", error.to_string()))?; }
        }
        Ok(())
    }

    fn backend(&self) -> AppResult<Arc<EngineBroker>> {
        self.broker.lock().expect("backend handle poisoned").clone().ok_or_else(|| CommandError::new("backend-unavailable", "音频后端不可用，请检查错误或明确重启后端"))
    }

    pub fn handle_engine_event(&self, generation: u64, event: &str, mut payload: Value) {
        if generation != self.generation.load(Ordering::Acquire) { return; }
        match event {
            "state" => {
                let mut cached = self.engine.lock().expect("engine state poisoned");
                let Some(engine) = cached.as_mut() else {
                    *self.pending_runtime.lock().expect("pending state poisoned") = Some(payload);
                    return;
                };
                let revision = self.revision.fetch_add(1, Ordering::AcqRel) + 1;
                payload["revision"] = Value::from(revision);
                let payload = Arc::new(payload);
                engine.runtime = payload.clone();
                drop(cached);
                self.refresh_tray();
                if let Err(error) = self.app.emit("engine_state", payload) { eprintln!("native event delivery: {error}"); }
            }
            "library" | "hotkeys" => {
                if let Some(engine) = self.engine.lock().expect("engine state poisoned").as_mut() {
                    if event == "library" { engine.library = Arc::new(payload); }
                    else { engine.hotkeys = Arc::new(payload); }
                }
                self.emit_changed();
            }
            "notice" => {
                let kind = payload.get("kind").and_then(Value::as_str).unwrap_or("Info");
                if let Some(message) = payload.get("message").and_then(Value::as_str) { self.notice(kind, message); }
            }
            "preset_applied" => {
                if payload.get("result").and_then(|result| result.get("accepted")).and_then(Value::as_bool) == Some(true) {
                    if let Some(id) = payload.get("presetId").and_then(Value::as_str) {
                        let result = {
                            let mut store = self.store.lock().expect("configuration store poisoned");
                            store.load_preset(id).and_then(|_| store.set_last_preset(Some(id)))
                        };
                        if let Err(error) = result { self.notice("Info", error.to_string()); }
                        self.emit_changed();
                    }
                }
            }
            _ => self.notice("Info", format!("未知后端事件: {event}")),
        }
    }

    pub fn handle_engine_stopped(&self, generation: u64, message: String) {
        if generation != self.generation.load(Ordering::Acquire) { return; }
        self.broker.lock().expect("backend handle poisoned").take();
        *self.engine.lock().expect("engine state poisoned") = None;
        *self.engine_error.lock().expect("engine error poisoned") = Some(Arc::new(message.clone()));
        self.refresh_tray();
        if let Err(error) = self.app.emit("engine_stopped", json!({"message":message})) { eprintln!("native event delivery: {error}"); }
        self.emit_changed();
    }

    pub fn report_error(&self, error: CommandError) {
        *self.engine_error.lock().expect("engine error poisoned") = Some(Arc::new(error.to_string()));
        self.notice("Info", error.to_string());
        self.emit_changed();
    }

    pub fn notice(&self, kind: &str, message: impl AsRef<str>) {
        #[derive(Clone, Serialize)]
        struct Notice<'a> { kind: &'a str, message: &'a str }
        let message = message.as_ref();
        if let Err(error) = self.app.emit("engine_notice", Notice { kind, message }) { eprintln!("native event delivery: {error}"); }
        use std::io::Write;
        let log_dir = self.root.join("logs");
        let log = log_dir.join("onbody.log");
        let written = (|| -> std::io::Result<()> {
            std::fs::create_dir_all(&log_dir)?;
            if std::fs::metadata(&log).is_ok_and(|metadata| metadata.len() > 256 * 1024) {
                let old = log_dir.join("onbody.log.old");
                if old.exists() { std::fs::remove_file(&old)?; }
                std::fs::rename(&log, old)?;
            }
            let mut output = std::fs::OpenOptions::new().create(true).append(true).open(log)?;
            writeln!(output, "[{kind}] {message}")
        })();
        if let Err(error) = written { eprintln!("local log: {error}"); }
    }

    fn emit_changed(&self) {
        if let Err(error) = self.app.emit("data_changed", ()) { eprintln!("native event delivery: {error}"); }
    }

    async fn sync_catalog(&self) {
        let loaded = {
            let store = self.store.lock().expect("configuration store poisoned");
            store.list_presets().and_then(|presets| presets.iter().filter(|preset| !preset.corrupt).map(|preset| store.load_preset(&preset.preset_id)).collect::<AppResult<Vec<_>>>())
        };
        match loaded {
            Ok(presets) => {
                if let Ok(broker) = self.backend() {
                    if let Err(error) = broker.request("set_catalog", json!({"presets":presets})).await { self.notice("Info", format!("预设已保存，但后端目录更新失败: {error}")); }
                }
            }
            Err(error) => self.notice("Info", error.to_string()),
        }
    }

    pub async fn command(&self, op: &str, mut args: Value) -> AppResult<Value> {
        match op {
            "set_dirty" => { self.dirty.store(required_bool(&args, "value")?, Ordering::Release); Ok(Value::Bool(true)) }
            "settings_save" => {
                let mut settings: SettingsData = decode_arg(&mut args, "settings")?;
                {
                    let mut store = self.store.lock().expect("configuration store poisoned");
                    settings.last_preset_id = store.settings().last_preset_id.clone();
                    store.save_settings(settings)?;
                }
                let saved = self.store.lock().expect("configuration store poisoned").settings_shared();
                if let Ok(broker) = self.backend() {
                    if let Err(error) = broker.request("settings_apply", json!({"settings":saved})).await { self.notice("Info", format!("配置已保存，后端激活失败: {error}")); }
                }
                self.emit_changed();
                Ok(Value::Bool(true))
            }
            "preset_load" => {
                let id = required_string(&args, "presetId")?;
                let loaded = self.store.lock().expect("configuration store poisoned").load_preset(id)?;
                serde_json::to_value(loaded).map_err(|error| CommandError::new("invalid-config", error.to_string()))
            }
            "preset_create" | "preset_save" => {
                let supplied = args.get_mut("config").filter(|config| config.is_object()).ok_or_else(|| CommandError::new("invalid-argument", "config 必须是配置对象"))?;
                if op == "preset_create" && supplied.get("presetId").and_then(Value::as_str).is_none_or(str::is_empty) {
                    supplied["presetId"] = Value::String(self.store.lock().expect("configuration store poisoned").new_preset_id());
                }
                let config: PresetConfig = decode_arg(&mut args, "config")?;
                self.store.lock().expect("configuration store poisoned").save_preset(&config)?;
                self.emit_changed();
                self.sync_catalog().await;
                self.dirty.store(false, Ordering::Release);
                if op == "preset_create" { serde_json::to_value(config).map_err(|error| CommandError::new("invalid-config", error.to_string())) }
                else { Ok(Value::Bool(true)) }
            }
            "preset_apply" => {
                let id = required_string(&args, "presetId")?;
                let config = self.store.lock().expect("configuration store poisoned").load_preset(id)?;
                let result = self.backend()?.request("apply_preset", json!({"config":config})).await?;
                if result.get("accepted").and_then(Value::as_bool) == Some(true) {
                    self.store.lock().expect("configuration store poisoned").set_last_preset(Some(id))?;
                    self.dirty.store(false, Ordering::Release);
                    self.emit_changed();
                }
                Ok(result)
            }
            "preset_rename" | "preset_duplicate" => {
                let id = required_string(&args, "presetId")?;
                let name = required_string(&args, "name")?;
                let result = {
                    let store = self.store.lock().expect("configuration store poisoned");
                    if op == "preset_rename" { store.rename_preset(id, name)?; Value::Bool(true) }
                    else { serde_json::to_value(store.duplicate_preset(id, name)?).map_err(|error| CommandError::new("invalid-config", error.to_string()))? }
                };
                self.emit_changed(); self.sync_catalog().await;
                Ok(result)
            }
            "preset_delete" => {
                let id = required_string(&args, "presetId")?;
                let active = self.engine.lock().expect("engine state poisoned").as_ref().and_then(|engine| engine.runtime.get("status")).and_then(|status| status.get("lastPresetId")).and_then(Value::as_str) == Some(id);
                if active { self.backend()?.request("clear_preset", json!({})).await?; }
                {
                    let mut store = self.store.lock().expect("configuration store poisoned");
                    store.delete_preset(id)?; store.remove_preset_metadata(id)?;
                }
                self.dirty.store(false, Ordering::Release);
                self.emit_changed(); self.sync_catalog().await;
                Ok(Value::Bool(true))
            }
            "preset_restore" => {
                self.store.lock().expect("configuration store poisoned").restore_preset_backup(required_string(&args, "presetId")?)?;
                self.emit_changed(); self.sync_catalog().await;
                Ok(Value::Bool(true))
            }
            "clip_references" => {
                let id = required_string(&args, "clipId")?;
                let store = self.store.lock().expect("configuration store poisoned");
                let mut references = Vec::new();
                for summary in store.list_presets()?.into_iter().filter(|summary| !summary.corrupt) {
                    let config = store.load_preset(&summary.preset_id)?;
                    if config.clip_layout.iter().any(|clip| clip == id) { references.push(json!({"presetId":summary.preset_id,"name":summary.name})); }
                }
                Ok(Value::Array(references))
            }
            "clip_delete" => {
                let id = required_string(&args, "clipId")?.to_owned();
                let result = self.backend()?.request(op, args).await?;
                self.store.lock().expect("configuration store poisoned").remove_clip_hotkeys(std::iter::once(id.as_str()))?;
                self.emit_changed();
                Ok(result)
            }
            "group_delete_contents" => {
                let result = self.backend()?.request(op, args).await?;
                let deleted = result.get("deletedClipIds").and_then(Value::as_array)
                    .filter(|ids| ids.iter().all(Value::is_string))
                    .ok_or_else(|| CommandError::new("protocol-error", "分组删除结果缺少音效 ID 列表"))?;
                let cleanup = self.store.lock().expect("configuration store poisoned")
                    .remove_clip_hotkeys(deleted.iter().map(|id| id.as_str().expect("validated clip ID")));
                self.emit_changed();
                cleanup.map_err(|error| CommandError::new("settings-save-failed", format!("分组及音效副本已删除，但热键清理未保存：{error}")))?;
                Ok(result)
            }
            "set_transparency" => {
                self.store.lock().expect("configuration store poisoned").set_transparency(required_bool(&args, "reduced")?)?;
                self.apply_transparency(); self.emit_changed(); Ok(Value::Bool(true))
            }
            "hide_window" => {
                self.store.lock().expect("configuration store poisoned").acknowledge_close_to_tray()?;
                if let Ok(broker) = self.backend() { broker.request("hotkeys_suspend", json!({"value":false})).await?; }
                if let Some(window) = self.app.get_webview_window("main") { window.hide().map_err(|error| CommandError::new("window-error", error.to_string()))?; }
                self.emit_changed(); Ok(Value::Bool(true))
            }
            "restart_engine" => { self.start_engine().await?; Ok(Value::Bool(true)) }
            "exit_app" => {
                self.exiting.store(true, Ordering::Release);
                let broker = self.broker.lock().expect("backend handle poisoned").take();
                if let Some(broker) = broker {
                    if let Err(error) = tokio::time::timeout(Duration::from_secs(3), broker.request("exit", json!({}))).await { self.notice("Info", format!("后端退出等待结束: {error}")); }
                    broker.terminate();
                }
                self.app.exit(0); Ok(Value::Bool(true))
            }
            "snapshot" | "devices" | "device_enable" | "apps" | "bind_app" | "route" | "gains" | "play" | "stop" | "mute" | "unlock" | "attach_output" | "prepare_folder_import" | "import_clip" | "inspect_audio" | "preview_audio" | "import_trimmed_audio" | "release_audio_preview" | "clip_rename" | "clip_favorite" | "clip_gain" | "clip_group" | "group_create" | "group_rename" | "group_delete" | "clip_path" | "hotkeys_suspend" => self.backend()?.request(op, args).await,
            _ => Err(CommandError::new("invalid-argument", format!("不支持的桌面操作: {op}"))),
        }
    }

    pub fn request_close(&self) {
        if self.store.lock().expect("configuration store poisoned").settings().ui.close_to_tray_acknowledged {
            let runtime = self.app.state::<Arc<Self>>().inner().clone();
            tauri::async_runtime::spawn(async move { if let Err(error) = runtime.command("hide_window", json!({})).await { runtime.notice("Info", error.to_string()); } });
        } else if let Err(error) = self.app.emit("confirm_close_to_tray", ()) { self.notice("Info", error.to_string()); }
    }

    pub fn request_exit(&self) {
        if self.dirty.load(Ordering::Acquire) {
            if let Some(window) = self.app.get_webview_window("main") {
                if let Err(error) = window.show().and_then(|_| window.set_focus()) { self.notice("Info", error.to_string()); }
            }
            if let Err(error) = self.app.emit("request_exit", ()) { self.notice("Info", error.to_string()); }
        } else {
            let runtime = self.app.state::<Arc<Self>>().inner().clone();
            tauri::async_runtime::spawn(async move { if let Err(error) = runtime.command("exit_app", json!({})).await { runtime.notice("Info", error.to_string()); } });
        }
    }

    fn apply_transparency(&self) {
        let reduced = self.store.lock().expect("configuration store poisoned").settings().ui.reduce_transparency;
        if let Some(window) = self.app.get_webview_window("main") {
            let effect = if reduced { None } else { Some(EffectsBuilder::new().effect(Effect::Acrylic).build()) };
            if let Err(error) = window.set_effects(effect) {
                if let Err(fallback) = window.set_background_color(Some(tauri::window::Color(16, 17, 20, 255))) { self.notice("Info", fallback.to_string()); }
                self.notice("Info", format!("系统透明材质不可用，使用不透明窗口: {error}"));
            }
        }
    }

    pub fn install_tray(self: &Arc<Self>) -> AppResult<()> {
        let app = &self.app;
        let show = MenuItem::with_id(app, "show", "打开随声麦", true, None::<&str>).map_err(native_error)?;
        let mic = CheckMenuItem::with_id(app, "mic", "真麦发送", false, false, None::<&str>).map_err(native_error)?;
        let extras = CheckMenuItem::with_id(app, "extras", "附加音频", false, false, None::<&str>).map_err(native_error)?;
        let monitor = CheckMenuItem::with_id(app, "monitor", "本地监听", false, false, None::<&str>).map_err(native_error)?;
        let mute = MenuItem::with_id(app, "mute", "全部静音", false, None::<&str>).map_err(native_error)?;
        let exit = MenuItem::with_id(app, "exit", "退出随声麦", true, None::<&str>).map_err(native_error)?;
        let menu = Menu::with_items(app, &[&show, &mic, &extras, &monitor, &mute, &exit]).map_err(native_error)?;
        let runtime = self.clone();
        let icon = app.default_window_icon().ok_or_else(|| CommandError::new("window-error", "应用图标不可用"))?.clone();
        let tray = TrayIconBuilder::with_id("onbodymic").icon(icon).menu(&menu).tooltip("随声麦").show_menu_on_left_click(false)
            .on_menu_event(move |_app, event| {
                let id = event.id.as_ref();
                if id == "show" {
                    if let Some(window) = runtime.app.get_webview_window("main") { if let Err(error) = window.show().and_then(|_| window.set_focus()) { runtime.notice("Info", error.to_string()); } }
                } else if id == "exit" { runtime.request_exit(); }
                else {
                    runtime.refresh_tray();
                    let action = id.to_owned();
                    let runtime = runtime.clone();
                    tauri::async_runtime::spawn(async move {
                        let (op, args) = {
                            let engine = runtime.engine.lock().expect("engine state poisoned");
                            if let Some(status) = engine.as_ref().and_then(|engine| engine.runtime.get("status")) {
                                if action == "mute" { (if status["emergencyLatched"] == true { "unlock" } else { "mute" }, json!({})) }
                                else {
                                    let field = match action.as_str() { "mic" => "micSendEnabled", "extras" => "extrasSendEnabled", _ => "monitorEnabled" };
                                    ("route", json!({"kind":action,"enabled":status["switches"][field] != true,"monitorMode":status["monitorMode"]}))
                                }
                            } else { return; }
                        };
                        if let Err(error) = runtime.command(op, args).await { runtime.notice("Info", error.to_string()); }
                    });
                }
            })
            .on_tray_icon_event(|tray, event| {
                if matches!(event, TrayIconEvent::Click { button: MouseButton::Left, button_state: MouseButtonState::Up, .. }) {
                    if let Some(window) = tray.app_handle().get_webview_window("main") {
                        if let Err(error) = window.show().and_then(|_| window.set_focus()) { tray.app_handle().state::<Arc<DesktopRuntime>>().notice("Info", error.to_string()); }
                    }
                }
            }).build(app).map_err(native_error)?;
        *self.tray.lock().expect("tray state poisoned") = Some(TrayState { _icon: tray, mic, extras, monitor, mute });
        Ok(())
    }

    fn refresh_tray(&self) {
        let engine = self.engine.lock().expect("engine state poisoned");
        let status = engine.as_ref().and_then(|engine| engine.runtime.get("status"));
        if let Some(tray) = self.tray.lock().expect("tray state poisoned").as_ref() {
            let enabled = status.is_some();
            for (item, field) in [(&tray.mic, "micSendEnabled"), (&tray.extras, "extrasSendEnabled"), (&tray.monitor, "monitorEnabled")] {
                if let Err(error) = item.set_enabled(enabled).and_then(|_| item.set_checked(status.is_some_and(|status| status["switches"][field] == true))) { eprintln!("tray state: {error}"); }
            }
            if let Err(error) = tray.mute.set_enabled(enabled).and_then(|_| tray.mute.set_text(if status.is_some_and(|status| status["emergencyLatched"] == true) { "解除静音锁存" } else { "全部静音" })) { eprintln!("tray state: {error}"); }
        }
    }
}

fn split_engine_snapshot(value: Value) -> AppResult<EngineView> {
    let Value::Object(object) = value else { return Err(CommandError::new("protocol-error", "后端状态不是对象")); };
    let mut runtime = Map::with_capacity(5);
    let mut library = Map::with_capacity(4);
    let mut hotkeys = Map::with_capacity(3);
    for (key, value) in object {
        if ["revision", "status", "levels", "metrics", "issues"].contains(&key.as_str()) { runtime.insert(key, value); }
        else if ["clips", "groups", "libraryError", "libraryRoot"].contains(&key.as_str()) { library.insert(key, value); }
        else if ["hotkeys", "hotkeyIssues", "hotkeyActions"].contains(&key.as_str()) { hotkeys.insert(key, value); }
    }
    if runtime.len() != 5 || library.len() != 4 || hotkeys.len() != 3 { return Err(CommandError::new("protocol-error", "后端状态缺少必要字段")); }
    Ok(EngineView { runtime: Arc::new(Value::Object(runtime)), library: Arc::new(Value::Object(library)), hotkeys: Arc::new(Value::Object(hotkeys)) })
}
fn required_string<'a>(args: &'a Value, key: &str) -> AppResult<&'a str> {
    args.get(key).and_then(Value::as_str).filter(|value| !value.is_empty()).ok_or_else(|| CommandError::new("invalid-argument", format!("缺少参数: {key}")))
}
fn required_bool(args: &Value, key: &str) -> AppResult<bool> {
    args.get(key).and_then(Value::as_bool).ok_or_else(|| CommandError::new("invalid-argument", format!("缺少布尔参数: {key}")))
}
fn decode_arg<T: serde::de::DeserializeOwned>(args: &mut Value, key: &str) -> AppResult<T> {
    serde_json::from_value(args.get_mut(key).map(Value::take).ok_or_else(|| CommandError::new("invalid-argument", format!("缺少参数: {key}")))?).map_err(|error| CommandError::new("invalid-argument", error.to_string()))
}
fn native_error(error: tauri::Error) -> CommandError { CommandError::new("window-error", error.to_string()) }
