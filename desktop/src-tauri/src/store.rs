use std::fs::{self, OpenOptions};
use std::io::{self, Write};
use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::{SystemTime, UNIX_EPOCH};
use serde::{Deserialize, Serialize};
use serde_json::Value;
use crate::models::{AppResult, AppSelection, CommandError, DeviceConfig, MonitorPreferences, PresetConfig, PresetSummary, SettingsData, SettingsIssue};

const SETTINGS_FIELDS: &[&str] = &["schemaVersion", "lastPresetId", "libraryRoot", "globalHotkeys", "clipHotkeys", "presetHotkeys", "ui"];
const SETTINGS_OBJECT_FIELDS: &[(&str, &[&str])] = &[
    ("/ui", &["startMinimized", "closeToTrayAcknowledged", "clipSort", "reduceTransparency"]),
];
const PRESET_FIELDS: &[&str] = &["schemaVersion", "presetId", "name", "application", "microphone", "appGain", "clipBus", "clipLayout", "libraryView", "virtualOutput", "monitor"];
const PRESET_OBJECT_FIELDS: &[(&str, &[&str])] = &[
    ("/application", &["selection"]),
    ("/application/selection", &["executablePath", "displayName"]),
    ("/microphone", &["deviceStableId", "gain"]),
    ("/clipBus", &["gain"]),
    ("/libraryView", &["favoritesOnly", "groupId"]),
    ("/virtualOutput", &["deviceStableId", "captureDeviceStableId"]),
    ("/monitor", &["mode", "deviceStableId", "gain"]),
];
const RUNTIME_FIELDS: &[&str] = &["micSendEnabled", "extrasSendEnabled", "appSendEnabled", "monitorEnabled", "anySendEnabled", "emergencyLatched", "appBinding", "outputLost", "clip"];

static NEXT_ID: AtomicU64 = AtomicU64::new(1);

pub struct Store {
    root: PathBuf,
    settings: Arc<SettingsData>,
    settings_error: Option<Arc<CommandError>>,
    settings_issues: Arc<Vec<SettingsIssue>>,
}

impl Store {
    pub fn open(root: PathBuf) -> AppResult<Self> {
        fs::create_dir_all(&root).map_err(|error| io_error(&root, error))?;
        let root = fs::canonicalize(&root).map_err(|error| io_error(&root, error))?;
        let path = root.join("settings.json");
        let loaded = load_settings(&path);
        let (settings, settings_error, settings_issues) = match loaded {
            Ok((settings, issues)) => (settings, None, issues),
            Err(error) if error.code == "not-found" => (SettingsData::default(), None, Vec::new()),
            Err(error) => (SettingsData::default(), Some(error), Vec::new()),
        };
        Ok(Self { root, settings: Arc::new(settings), settings_error: settings_error.map(Arc::new), settings_issues: Arc::new(settings_issues) })
    }

    pub fn root(&self) -> &Path { &self.root }
    pub fn settings(&self) -> &SettingsData { &self.settings }
    pub fn settings_shared(&self) -> Arc<SettingsData> { self.settings.clone() }
    pub fn settings_error_shared(&self) -> Option<Arc<CommandError>> { self.settings_error.clone() }
    pub fn settings_issues_shared(&self) -> Arc<Vec<SettingsIssue>> { self.settings_issues.clone() }

    pub fn set_last_preset(&mut self, id: Option<&str>) -> AppResult<()> {
        if self.settings.last_preset_id.as_deref() == id { return Ok(()); }
        let previous = Arc::make_mut(&mut self.settings).last_preset_id.take();
        Arc::make_mut(&mut self.settings).last_preset_id = id.map(str::to_owned);
        if let Err(error) = self.persist_current() {
            Arc::make_mut(&mut self.settings).last_preset_id = previous;
            return Err(error);
        }
        Ok(())
    }

    pub fn remove_clip_hotkeys<'a>(&mut self, ids: impl IntoIterator<Item = &'a str>) -> AppResult<()> {
        let mut removed = Vec::new();
        for id in ids {
            if let Some(entry) = Arc::make_mut(&mut self.settings).clip_hotkeys.shift_remove_full(id) {
                removed.push(entry);
            }
        }
        if removed.is_empty() { return Ok(()); }
        if let Err(error) = self.persist_current() {
            let hotkeys = &mut Arc::make_mut(&mut self.settings).clip_hotkeys;
            for (index, key, value) in removed.into_iter().rev() { hotkeys.shift_insert(index, key, value); }
            return Err(error);
        }
        Ok(())
    }

    pub fn remove_preset_metadata(&mut self, id: &str) -> AppResult<()> {
        let settings = Arc::make_mut(&mut self.settings);
        let removed = settings.preset_hotkeys.shift_remove_full(id);
        let previous_id = if settings.last_preset_id.as_deref() == Some(id) { settings.last_preset_id.take() } else { None };
        if removed.is_none() && previous_id.is_none() { return Ok(()); }
        if let Err(error) = self.persist_current() {
            let settings = Arc::make_mut(&mut self.settings);
            if let Some((index, key, value)) = removed { settings.preset_hotkeys.shift_insert(index, key, value); }
            if previous_id.is_some() { settings.last_preset_id = previous_id; }
            return Err(error);
        }
        Ok(())
    }

    pub fn set_transparency(&mut self, reduced: bool) -> AppResult<()> {
        let previous = self.settings.ui.reduce_transparency;
        Arc::make_mut(&mut self.settings).ui.reduce_transparency = reduced;
        if let Err(error) = self.persist_current() {
            Arc::make_mut(&mut self.settings).ui.reduce_transparency = previous;
            return Err(error);
        }
        Ok(())
    }

    pub fn acknowledge_close_to_tray(&mut self) -> AppResult<()> {
        let previous = self.settings.ui.close_to_tray_acknowledged;
        Arc::make_mut(&mut self.settings).ui.close_to_tray_acknowledged = true;
        if let Err(error) = self.persist_current() {
            Arc::make_mut(&mut self.settings).ui.close_to_tray_acknowledged = previous;
            return Err(error);
        }
        Ok(())
    }

    fn persist_current(&mut self) -> AppResult<()> {
        let path = self.root.join("settings.json");
        let bytes = serialize_preserving(self.settings.as_ref(), existing_document(&path)?, SETTINGS_FIELDS, SETTINGS_OBJECT_FIELDS)?;
        atomic_write(&path, &bytes)?;
        self.settings_error = None;
        Ok(())
    }

    pub fn save_settings(&mut self, mut data: SettingsData) -> AppResult<()> {
        if data.schema_version != 1 {
            return Err(CommandError::new("unsupported-schema", "配置 schemaVersion 必须为 1"));
        }
        let mut issues = Vec::new();
        if !matches!(data.ui.clip_sort.as_str(), "name" | "importTime" | "libraryOrder") {
            issues.push(SettingsIssue { section: "ui".into(), key: "clipSort".into(), reason: "未知排序方式，使用名称排序".into() });
            data.ui.clip_sort = "name".into();
        }
        let path = self.root.join("settings.json");
        let bytes = serialize_preserving(&data, existing_document(&path)?, SETTINGS_FIELDS, SETTINGS_OBJECT_FIELDS)?;
        atomic_write(&path, &bytes)?;
        self.settings = Arc::new(data);
        self.settings_error = None;
        self.settings_issues = Arc::new(issues);
        Ok(())
    }

    pub fn list_presets(&self) -> AppResult<Vec<PresetSummary>> {
        let directory = self.root.join("presets");
        let entries = match fs::read_dir(&directory) {
            Ok(entries) => entries,
            Err(error) if error.kind() == io::ErrorKind::NotFound => return Ok(Vec::new()),
            Err(error) => return Err(io_error(&directory, error)),
        };
        let mut result = Vec::new();
        for entry in entries {
            let entry = entry.map_err(|error| io_error(&directory, error))?;
            let path = entry.path();
            if path.extension().and_then(|value| value.to_str()) != Some("json") { continue; }
            let Some(id) = path.file_stem().and_then(|value| value.to_str()) else { continue; };
            match self.load_preset(id) {
                Ok(config) => result.push(PresetSummary { preset_id: config.preset_id, name: config.name, corrupt: false, reason: None }),
                Err(error) => result.push(PresetSummary { preset_id: id.into(), name: id.into(), corrupt: true, reason: Some(error.to_string()) }),
            }
        }
        result.sort_unstable_by(|left, right| left.preset_id.cmp(&right.preset_id));
        Ok(result)
    }

    pub fn load_preset(&self, id: &str) -> AppResult<PresetConfig> {
        let path = self.preset_path(id)?;
        parse_preset(id, read_json(&path)?)
    }

    pub fn save_preset(&self, config: &PresetConfig) -> AppResult<()> {
        validate_preset(config)?;
        let path = self.preset_path(&config.preset_id)?;
        let bytes = serialize_preserving(&PresetDiskView::new(config), existing_document(&path)?, PRESET_FIELDS, PRESET_OBJECT_FIELDS)?;
        atomic_write(&path, &bytes)
    }

    pub fn new_preset_id(&self) -> String {
        loop {
            let stamp = SystemTime::now().duration_since(UNIX_EPOCH).map_or(0, |duration| duration.as_nanos());
            let serial = NEXT_ID.fetch_add(1, Ordering::Relaxed);
            let id = format!("preset-{stamp:x}-{serial:x}");
            if !self.root.join("presets").join(format!("{id}.json")).exists() { return id; }
        }
    }

    pub fn delete_preset(&self, id: &str) -> AppResult<()> {
        let path = self.preset_path(id)?;
        for suffix in [".tmp", ".bak"] {
            let side = with_suffix(&path, suffix);
            match fs::remove_file(&side) {
                Ok(()) => (),
                Err(error) if error.kind() == io::ErrorKind::NotFound => (),
                Err(error) => return Err(io_error(&side, error)),
            }
        }
        fs::remove_file(&path).map_err(|error| io_error(&path, error))?;
        Ok(())
    }

    pub fn rename_preset(&self, id: &str, name: &str) -> AppResult<()> {
        let mut config = self.load_preset(id)?;
        config.name = name.trim().into();
        self.save_preset(&config)
    }

    pub fn duplicate_preset(&self, id: &str, name: &str) -> AppResult<PresetConfig> {
        let mut source = read_json(&self.preset_path(id)?)?;
        normalize_document(&mut source, PRESET_FIELDS, PRESET_OBJECT_FIELDS);
        let disk = DiskPreset::deserialize(&source).map_err(|error| CommandError::new("preset-corrupt", error.to_string()))?;
        let mut config = preset_from_disk(id, disk)?;
        config.preset_id = self.new_preset_id();
        config.name = name.trim().into();
        validate_preset(&config)?;
        let bytes = serialize_preserving(&PresetDiskView::new(&config), Some(source), PRESET_FIELDS, PRESET_OBJECT_FIELDS)?;
        atomic_write(&self.preset_path(&config.preset_id)?, &bytes)?;
        Ok(config)
    }

    pub fn restore_preset_backup(&self, id: &str) -> AppResult<()> {
        let path = self.preset_path(id)?;
        let backup = with_suffix(&path, ".bak");
        let bytes = fs::read(&backup).map_err(|error| io_error(&backup, error))?;
        parse_preset(id, decode_json(&bytes)?)?;
        atomic_write(&path, &bytes)
    }

    fn preset_path(&self, id: &str) -> AppResult<PathBuf> {
        if id.is_empty() || id.len() > 64 || !id.bytes().all(|value| value.is_ascii_alphanumeric() || value == b'-' || value == b'_') {
            return Err(CommandError::new("invalid-preset-id", "预设 ID 必须是 1–64 位字母、数字、下划线或短横线"));
        }
        Ok(self.root.join("presets").join(format!("{id}.json")))
    }
}

pub fn validate_preset(config: &PresetConfig) -> AppResult<()> {
    let id = &config.preset_id;
    if id.is_empty() || id.len() > 64 || !id.bytes().all(|value| value.is_ascii_alphanumeric() || value == b'-' || value == b'_') {
        return Err(CommandError::new("invalid-preset-id", "预设 ID 无效"));
    }
    if config.name.trim().is_empty() { return Err(CommandError::new("name-required", "请输入预设名称")); }
    if config.devices.mic_device_id.trim().is_empty() || config.devices.virtual_render_device_id.trim().is_empty() || config.devices.monitor_device_id.trim().is_empty() {
        return Err(CommandError::new("device-id-required", "需要明确选择麦克风、虚拟输出和监听耳机"));
    }
    if [config.mic_gain, config.app_gain, config.clip_gain, config.monitor.gain].iter().any(|gain| !gain.is_finite() || *gain < 0.0 || *gain > 4.0) {
        return Err(CommandError::new("gain-out-of-range", "增益必须在 0–400% 之间"));
    }
    Ok(())
}

fn load_settings(path: &Path) -> AppResult<(SettingsData, Vec<SettingsIssue>)> {
    let mut value = read_json(path)?;
    normalize_document(&mut value, SETTINGS_FIELDS, SETTINGS_OBJECT_FIELDS);
    let mut issues = Vec::new();
    if let Some(ui) = value.get_mut("ui") {
        if ui.get("clipSort").is_some_and(|sort| !matches!(sort.as_str(), Some("name" | "importTime" | "libraryOrder"))) {
            ui["clipSort"] = Value::String("name".into());
            issues.push(SettingsIssue { section: "ui".into(), key: "clipSort".into(), reason: "未知排序方式，使用名称排序".into() });
        }
    }
    let data: SettingsData = serde_json::from_value(value).map_err(|error| CommandError::new("config-corrupt", error.to_string()))?;
    if data.schema_version != 1 { return Err(CommandError::new("unsupported-schema", "不支持此配置 schemaVersion；原文件未改动")); }
    Ok((data, issues))
}

fn normalize(value: &mut Value, names: &[&str]) {
    if let Value::Object(object) = value {
        let original = std::mem::take(object);
        for (key, value) in original {
            let canonical = names.iter().find(|name| name.eq_ignore_ascii_case(&key));
            let key = match canonical { Some(name) if *name != key => (*name).to_owned(), _ => key };
            object.insert(key, value);
        }
    }
}

fn normalize_document(value: &mut Value, fields: &[&str], children: &[(&str, &[&str])]) {
    normalize(value, fields);
    for (pointer, names) in children {
        if let Some(child) = value.pointer_mut(pointer) { normalize(child, names); }
    }
}

fn existing_document(path: &Path) -> AppResult<Option<Value>> {
    match read_json(path) {
        Ok(value) => Ok(Some(value)),
        Err(error) if error.code == "not-found" => Ok(None),
        Err(error) => Err(error),
    }
}

// Unknown metadata belongs to the disk document, not the public DTO. Known maps,
// omitted references and runtime switches must never be resurrected by this merge.
fn serialize_preserving<T: Serialize>(data: &T, previous: Option<Value>, fields: &[&str], children: &[(&str, &[&str])]) -> AppResult<Vec<u8>> {
    let mut document = serde_json::to_value(data).map_err(|error| CommandError::new("invalid-config", error.to_string()))?;
    if let Some(mut previous) = previous {
        if !previous.is_object() { return Err(CommandError::new("config-corrupt", "旧配置不是 JSON 对象；原文件未改动")); }
        normalize_document(&mut previous, fields, children);
        if previous.get("schemaVersion").is_some_and(|version| version.as_u64() != Some(1)) {
            return Err(CommandError::new("unsupported-schema", "不支持旧配置 schemaVersion；原文件未改动"));
        }
        // Normalize parents first, then move nested extensions before their parents.
        for (pointer, names) in children.iter().rev() {
            if let (Some(old), Some(current)) = (previous.pointer_mut(pointer), document.pointer_mut(pointer)) {
                preserve_unknown_fields(current, old.take(), names);
            }
        }
        preserve_unknown_fields(&mut document, previous, fields);
    }
    if let Some(root) = document.as_object_mut() {
        root.retain(|key, _| !RUNTIME_FIELDS.iter().any(|name| name.eq_ignore_ascii_case(key)));
    }
    serde_json::to_vec_pretty(&document).map_err(|error| CommandError::new("invalid-config", error.to_string()))
}

fn preserve_unknown_fields(current: &mut Value, previous: Value, fields: &[&str]) {
    if let (Value::Object(current), Value::Object(previous)) = (current, previous) {
        for (key, value) in previous {
            if !fields.iter().any(|name| name.eq_ignore_ascii_case(&key)) {
                current.entry(key).or_insert(value);
            }
        }
    }
}

fn parse_preset(id: &str, mut value: Value) -> AppResult<PresetConfig> {
    normalize_document(&mut value, PRESET_FIELDS, PRESET_OBJECT_FIELDS);
    let disk: DiskPreset = serde_json::from_value(value).map_err(|error| CommandError::new("preset-corrupt", error.to_string()))?;
    preset_from_disk(id, disk)
}

fn preset_from_disk(id: &str, disk: DiskPreset) -> AppResult<PresetConfig> {
    if disk.schema_version != 1 { return Err(CommandError::new("unsupported-schema", "不支持此预设 schemaVersion；原文件未改动")); }
    if disk.preset_id.as_deref().is_some_and(|file_id| !file_id.is_empty() && file_id != id) {
        return Err(CommandError::new("preset-id-mismatch", "预设文件名与内容 ID 不一致"));
    }
    let microphone = disk.microphone.unwrap_or_default();
    let output = disk.virtual_output.unwrap_or_default();
    let monitor = disk.monitor.unwrap_or_default();
    let library = disk.library_view.unwrap_or_default();
    let config = PresetConfig {
        preset_id: id.into(), name: disk.name.unwrap_or_default(),
        devices: DeviceConfig {
            mic_device_id: microphone.device_stable_id.unwrap_or_default(),
            virtual_render_device_id: output.device_stable_id.unwrap_or_default(),
            virtual_capture_device_id: output.capture_device_stable_id,
            monitor_device_id: monitor.device_stable_id.unwrap_or_default(),
            application: disk.application.and_then(|application| application.selection),
        },
        mic_gain: microphone.gain.unwrap_or(1.0), app_gain: disk.app_gain.unwrap_or(1.0),
        clip_gain: disk.clip_bus.and_then(|bus| bus.gain).unwrap_or(1.0),
        monitor: MonitorPreferences { mode: if monitor.mode.as_deref() == Some("FullEndpoint") { "FullEndpoint".into() } else { "ClipOnly".into() }, gain: monitor.gain.unwrap_or(1.0) },
        clip_layout: disk.clip_layout.unwrap_or_default(), library_favorites_only: library.favorites_only,
        library_group_id: library.group_id,
    };
    validate_preset(&config)?;
    Ok(config)
}

#[derive(Default, Serialize, Deserialize)]
#[serde(default, rename_all = "camelCase")]
struct DiskPreset {
    #[serde(default = "schema_one")]
    schema_version: u32,
    #[serde(skip_serializing_if = "Option::is_none")] preset_id: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")] name: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")] application: Option<DiskApplication>,
    #[serde(skip_serializing_if = "Option::is_none")] microphone: Option<DiskMicrophone>,
    #[serde(skip_serializing_if = "Option::is_none")] app_gain: Option<f32>,
    #[serde(skip_serializing_if = "Option::is_none")] clip_bus: Option<DiskBus>,
    #[serde(skip_serializing_if = "Option::is_none")] clip_layout: Option<Vec<String>>,
    #[serde(skip_serializing_if = "Option::is_none")] library_view: Option<DiskLibrary>,
    #[serde(skip_serializing_if = "Option::is_none")] virtual_output: Option<DiskOutput>,
    #[serde(skip_serializing_if = "Option::is_none")] monitor: Option<DiskMonitor>,
}
fn schema_one() -> u32 { 1 }
#[derive(Default, Serialize, Deserialize)]
#[serde(default, rename_all = "camelCase")]
struct DiskApplication { #[serde(skip_serializing_if = "Option::is_none")] selection: Option<AppSelection> }
#[derive(Default, Serialize, Deserialize)]
#[serde(default, rename_all = "camelCase")]
struct DiskMicrophone { #[serde(skip_serializing_if = "Option::is_none")] device_stable_id: Option<String>, #[serde(skip_serializing_if = "Option::is_none")] gain: Option<f32> }
#[derive(Default, Serialize, Deserialize)]
#[serde(default, rename_all = "camelCase")]
struct DiskBus { #[serde(skip_serializing_if = "Option::is_none")] gain: Option<f32> }
#[derive(Default, Serialize, Deserialize)]
#[serde(default, rename_all = "camelCase")]
struct DiskLibrary { favorites_only: bool, #[serde(skip_serializing_if = "Option::is_none")] group_id: Option<String> }
#[derive(Default, Serialize, Deserialize)]
#[serde(default, rename_all = "camelCase")]
struct DiskOutput { #[serde(skip_serializing_if = "Option::is_none")] device_stable_id: Option<String>, #[serde(skip_serializing_if = "Option::is_none")] capture_device_stable_id: Option<String> }
#[derive(Default, Serialize, Deserialize)]
#[serde(default, rename_all = "camelCase")]
struct DiskMonitor { #[serde(skip_serializing_if = "Option::is_none")] mode: Option<String>, #[serde(skip_serializing_if = "Option::is_none")] device_stable_id: Option<String>, #[serde(skip_serializing_if = "Option::is_none")] gain: Option<f32> }

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct PresetDiskView<'a> {
    schema_version: u32,
    preset_id: &'a str,
    name: &'a str,
    #[serde(skip_serializing_if = "Option::is_none")]
    application: Option<ApplicationView<'a>>,
    microphone: MicrophoneView<'a>,
    app_gain: f32,
    clip_bus: BusView,
    clip_layout: &'a [String],
    library_view: LibraryView<'a>,
    virtual_output: OutputView<'a>,
    monitor: MonitorView<'a>,
}
#[derive(Serialize)]
struct ApplicationView<'a> { selection: SelectionView<'a> }
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct SelectionView<'a> {
    #[serde(skip_serializing_if = "Option::is_none")] executable_path: Option<&'a str>,
    #[serde(skip_serializing_if = "Option::is_none")] display_name: Option<&'a str>,
}
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct MicrophoneView<'a> { device_stable_id: &'a str, gain: f32 }
#[derive(Serialize)]
struct BusView { gain: f32 }
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct LibraryView<'a> { favorites_only: bool, #[serde(skip_serializing_if = "Option::is_none")] group_id: Option<&'a str> }
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct OutputView<'a> { device_stable_id: &'a str, #[serde(skip_serializing_if = "Option::is_none")] capture_device_stable_id: Option<&'a str> }
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct MonitorView<'a> { mode: &'static str, device_stable_id: &'a str, gain: f32 }
impl<'a> PresetDiskView<'a> {
    fn new(config: &'a PresetConfig) -> Self {
        Self {
            schema_version: 1, preset_id: &config.preset_id, name: &config.name,
            application: config.devices.application.as_ref().filter(|selection| selection.executable_path.as_ref().is_some_and(|path| !path.trim().is_empty())).map(|selection| ApplicationView { selection: SelectionView { executable_path: selection.executable_path.as_deref(), display_name: selection.display_name.as_deref() } }),
            microphone: MicrophoneView { device_stable_id: &config.devices.mic_device_id, gain: config.mic_gain },
            app_gain: config.app_gain, clip_bus: BusView { gain: config.clip_gain },
            clip_layout: &config.clip_layout,
            library_view: LibraryView { favorites_only: config.library_favorites_only, group_id: config.library_group_id.as_deref() },
            virtual_output: OutputView { device_stable_id: &config.devices.virtual_render_device_id, capture_device_stable_id: config.devices.virtual_capture_device_id.as_deref() },
            monitor: MonitorView { mode: if config.monitor.mode == "FullEndpoint" { "FullEndpoint" } else { "ClipOnly" }, device_stable_id: &config.devices.monitor_device_id, gain: config.monitor.gain },
        }
    }
}

fn read_json(path: &Path) -> AppResult<Value> {
    let bytes = fs::read(path).map_err(|error| io_error(path, error))?;
    decode_json(&bytes)
}
fn decode_json(bytes: &[u8]) -> AppResult<Value> {
    let bytes = bytes.strip_prefix(&[0xef, 0xbb, 0xbf]).unwrap_or(bytes);
    if bytes.starts_with(&[0xff, 0xfe]) || bytes.starts_with(&[0xfe, 0xff]) {
        let little = bytes[0] == 0xff;
        let body = &bytes[2..];
        if body.len() % 2 != 0 { return Err(CommandError::new("json-corrupt", "UTF-16 配置长度无效")); }
        let text: Result<String, _> = char::decode_utf16(body.chunks_exact(2).map(|pair| if little { u16::from_le_bytes([pair[0], pair[1]]) } else { u16::from_be_bytes([pair[0], pair[1]]) })).collect();
        return serde_json::from_str(&text.map_err(|error| CommandError::new("json-corrupt", error.to_string()))?).map_err(|error| CommandError::new("json-corrupt", error.to_string()));
    }
    serde_json::from_slice(bytes).map_err(|error| CommandError::new("json-corrupt", error.to_string()))
}
fn with_suffix(path: &Path, suffix: &str) -> PathBuf {
    let mut name = path.as_os_str().to_os_string();
    name.push(suffix);
    PathBuf::from(name)
}
fn io_error(path: &Path, error: io::Error) -> CommandError {
    CommandError::new(if error.kind() == io::ErrorKind::NotFound { "not-found" } else { "io-error" }, format!("{}: {error}", path.display()))
}

fn atomic_write(path: &Path, contents: &[u8]) -> AppResult<()> {
    let directory = path.parent().ok_or_else(|| CommandError::new("invalid-path", "配置路径没有父目录"))?;
    fs::create_dir_all(directory).map_err(|error| io_error(directory, error))?;
    let temporary = with_suffix(path, ".tmp");
    let result = (|| {
        let mut file = OpenOptions::new().create(true).truncate(true).write(true).open(&temporary).map_err(|error| io_error(&temporary, error))?;
        file.write_all(contents).map_err(|error| io_error(&temporary, error))?;
        file.sync_all().map_err(|error| io_error(&temporary, error))?;
        drop(file);
        let verified = fs::read(&temporary).map_err(|error| io_error(&temporary, error))?;
        if verified != contents { return Err(CommandError::new("write-verify-failed", "配置临时文件校验失败，旧文件保留")); }
        if path.exists() { replace_with_backup(path, &temporary, &with_suffix(path, ".bak"))?; }
        else { fs::rename(&temporary, path).map_err(|error| io_error(path, error))?; }
        Ok(())
    })();
    if temporary.exists() {
        if let Err(error) = fs::remove_file(&temporary) {
            if result.is_ok() { return Err(io_error(&temporary, error)); }
        }
    }
    result
}

#[cfg(windows)]
fn replace_with_backup(target: &Path, replacement: &Path, backup: &Path) -> AppResult<()> {
    use std::os::windows::ffi::OsStrExt;
    use windows_sys::Win32::Storage::FileSystem::ReplaceFileW;
    let target_wide: Vec<u16> = target.as_os_str().encode_wide().chain(Some(0)).collect();
    let replacement_wide: Vec<u16> = replacement.as_os_str().encode_wide().chain(Some(0)).collect();
    let backup_wide: Vec<u16> = backup.as_os_str().encode_wide().chain(Some(0)).collect();
    let success = unsafe { ReplaceFileW(target_wide.as_ptr(), replacement_wide.as_ptr(), backup_wide.as_ptr(), 0, std::ptr::null_mut(), std::ptr::null_mut()) };
    if success == 0 { return Err(io_error(target, io::Error::last_os_error())); }
    Ok(())
}
#[cfg(not(windows))]
fn replace_with_backup(target: &Path, replacement: &Path, backup: &Path) -> AppResult<()> {
    fs::copy(target, backup).map_err(|error| io_error(backup, error))?;
    fs::rename(replacement, target).map_err(|error| io_error(target, error))
}

#[cfg(test)]
mod tests {
    use super::*;
    use tempfile::tempdir;

    fn preset(id: &str, name: &str) -> PresetConfig {
        PresetConfig { preset_id: id.into(), name: name.into(), devices: DeviceConfig { mic_device_id: "mic-id".into(), virtual_render_device_id: "render-id".into(), virtual_capture_device_id: Some("capture-id".into()), monitor_device_id: "headphones-id".into(), application: Some(AppSelection { executable_path: Some("C:\\Apps\\音乐.exe".into()), display_name: Some("音乐播放器".into()) }) }, mic_gain: 1.25, app_gain: 0.7, clip_gain: 1.0, monitor: MonitorPreferences { mode: "FullEndpoint".into(), gain: 0.65 }, clip_layout: vec!["clip-stable-id".into()], library_favorites_only: true, library_group_id: Some("group-stable-id".into()) }
    }

    #[test]
    fn deleting_multiple_clips_preserves_unrelated_bindings_after_reopen() {
        let directory = tempdir().unwrap();
        let path = directory.path().join("settings.json");
        fs::write(&path, br#"{"schemaVersion":1,"clipHotkeys":{"clip-a":"A","outside":"F9","clip-b":"B"},"globalHotkeys":{},"presetHotkeys":{},"ui":{}}"#).unwrap();
        let mut store = Store::open(directory.path().into()).unwrap();
        store.remove_clip_hotkeys(["clip-b", "missing", "clip-a"]).unwrap();
        let reopened = Store::open(directory.path().into()).unwrap();
        assert_eq!(reopened.settings().clip_hotkeys.keys().map(String::as_str).collect::<Vec<_>>(), vec!["outside"]);
        assert_eq!(reopened.settings().clip_hotkeys["outside"].as_deref(), Some("F9"));
    }

    #[test]
    fn failed_batch_cleanup_restores_binding_values_and_original_order() {
        let directory = tempdir().unwrap();
        let path = directory.path().join("settings.json");
        let original = br#"{"schemaVersion":1,"clipHotkeys":{"clip-a":"A","clip-b":null,"outside":"F9","clip-c":"C"},"globalHotkeys":{},"presetHotkeys":{},"ui":{}}"#;
        fs::write(&path, original).unwrap();
        let mut store = Store::open(directory.path().into()).unwrap();
        let retained = directory.path().join("retained-settings.json");
        fs::rename(&path, &retained).unwrap();
        fs::create_dir(&path).unwrap();
        assert!(store.remove_clip_hotkeys(["clip-c", "clip-a", "clip-b"]).is_err());
        assert_eq!(store.settings().clip_hotkeys.keys().map(String::as_str).collect::<Vec<_>>(), vec!["clip-a", "clip-b", "outside", "clip-c"]);
        assert_eq!(store.settings().clip_hotkeys["clip-a"].as_deref(), Some("A"));
        assert_eq!(store.settings().clip_hotkeys["clip-b"], None);
        assert_eq!(store.settings().clip_hotkeys["clip-c"].as_deref(), Some("C"));
        assert_eq!(fs::read(&retained).unwrap(), original);
    }

    #[test]
    fn settings_roundtrip_preserves_extensions_without_reviving_cleared_state() {
        let directory = tempdir().unwrap();
        let path = directory.path().join("settings.json");
        let original = serde_json::json!({
            "schemaVersion": 1, "lastPresetId": "night", "libraryRoot": "C:\\旧素材",
            "globalHotkeys": {"emergencyMute": "Ctrl+Alt+F11"},
            "clipHotkeys": {"clip-id": "Alt+1"}, "presetHotkeys": {"night": "Alt+2"},
            "ui": {"clipSort": "name", "futureLayout": {"label": "旧布局", "width": 240}},
            "futureCompatibility": {"label": "必须保留", "values": [null, 3, true]},
            "micSendEnabled": true, "monitorEnabled": true
        });
        fs::write(&path, serde_json::to_vec(&original).unwrap()).unwrap();
        let mut store = Store::open(directory.path().into()).unwrap();
        let mut submitted: SettingsData = serde_json::from_value(serde_json::to_value(store.settings()).unwrap()).unwrap();
        submitted.last_preset_id = None;
        submitted.library_root = None;
        submitted.global_hotkeys.clear();
        submitted.clip_hotkeys.clear();
        submitted.preset_hotkeys.clear();
        submitted.ui.reduce_transparency = true;
        store.save_settings(submitted).unwrap();
        let written = read_json(&path).unwrap();
        assert_eq!(written["futureCompatibility"], original["futureCompatibility"]);
        assert_eq!(written["ui"]["futureLayout"], original["ui"]["futureLayout"]);
        assert_eq!(written["globalHotkeys"], serde_json::json!({}));
        assert_eq!(written["clipHotkeys"], serde_json::json!({}));
        assert_eq!(written["presetHotkeys"], serde_json::json!({}));
        assert!(written.get("micSendEnabled").is_none());
        assert!(written.get("monitorEnabled").is_none());
        let reopened = Store::open(directory.path().into()).unwrap();
        assert_eq!(reopened.settings().last_preset_id, None);
        assert_eq!(reopened.settings().library_root, None);
        assert!(reopened.settings().ui.reduce_transparency);
        store.set_transparency(false).unwrap();
        let persisted = read_json(&path).unwrap();
        assert_eq!(persisted["futureCompatibility"], original["futureCompatibility"]);
        assert_eq!(persisted["ui"]["futureLayout"], original["ui"]["futureLayout"]);
    }

    #[test]
    fn preset_roundtrip_and_duplicate_preserve_extensions_without_reviving_references() {
        let directory = tempdir().unwrap();
        let store = Store::open(directory.path().into()).unwrap();
        let path = directory.path().join("presets/night.json");
        fs::create_dir_all(path.parent().unwrap()).unwrap();
        let mut original = serde_json::to_value(PresetDiskView::new(&preset("night", "夜间"))).unwrap();
        original["futureCompatibility"] = serde_json::json!({"label": "旧预设扩展"});
        original["microphone"]["futureDevice"] = serde_json::json!({"channel": 2});
        original["application"]["futureApplication"] = serde_json::json!("保留应用元数据");
        original["application"]["selection"]["futureSelection"] = serde_json::json!({"instance": "用户实例"});
        original["virtualOutput"]["futureRoute"] = serde_json::json!("旧路由");
        original["libraryView"]["futureGrouping"] = serde_json::json!(["a", "b"]);
        let microphone = original.as_object_mut().unwrap().remove("microphone").unwrap();
        original["MICROPHONE"] = microphone;
        fs::write(&path, serde_json::to_vec(&original).unwrap()).unwrap();
        let mut submitted: PresetConfig = serde_json::from_value(serde_json::to_value(store.load_preset("night").unwrap()).unwrap()).unwrap();
        submitted.mic_gain = 2.0;
        submitted.devices.virtual_capture_device_id = None;
        submitted.devices.application.as_mut().unwrap().display_name = None;
        submitted.library_group_id = None;
        store.save_preset(&submitted).unwrap();
        let written = read_json(&path).unwrap();
        assert_eq!(written["futureCompatibility"], original["futureCompatibility"]);
        assert_eq!(written["microphone"]["futureDevice"], original["MICROPHONE"]["futureDevice"]);
        assert_eq!(written["microphone"]["gain"], 2.0);
        assert!(written.get("MICROPHONE").is_none());
        assert_eq!(written["application"]["futureApplication"], original["application"]["futureApplication"]);
        assert_eq!(written["application"]["selection"]["futureSelection"], original["application"]["selection"]["futureSelection"]);
        assert!(written["application"]["selection"].get("displayName").is_none());
        assert_eq!(written["virtualOutput"]["futureRoute"], original["virtualOutput"]["futureRoute"]);
        assert!(written["virtualOutput"].get("captureDeviceStableId").is_none());
        assert_eq!(written["libraryView"]["futureGrouping"], original["libraryView"]["futureGrouping"]);
        assert!(written["libraryView"].get("groupId").is_none());
        let duplicate = store.duplicate_preset("night", "复制预设").unwrap();
        let copied = read_json(&directory.path().join("presets").join(format!("{}.json", duplicate.preset_id))).unwrap();
        assert_eq!(copied["presetId"], duplicate.preset_id);
        assert_eq!(copied["futureCompatibility"], written["futureCompatibility"]);
        assert_eq!(copied["microphone"]["futureDevice"], written["microphone"]["futureDevice"]);
        assert_eq!(copied["application"]["selection"]["futureSelection"], written["application"]["selection"]["futureSelection"]);
        submitted.devices.application = None;
        store.save_preset(&submitted).unwrap();
        assert!(read_json(&path).unwrap().get("application").is_none());
        assert_eq!(store.load_preset("night").unwrap(), submitted);
    }

    #[test]
    fn manual_legacy_settings_keep_order_unknown_bindings_and_unicode() {
        let directory = tempdir().unwrap();
        let source = "\u{feff}{\"SCHEMAVERSION\":1,\"LASTPRESETID\":\"night\",\"GLOBALHOTKEYS\":{\"unknownAction\":\"Ctrl+M\",\"emergencyMute\":null},\"CLIPHOTKEYS\":{\"clip-b\":\"Alt+2\",\"clip-a\":\"Alt+1\"},\"UI\":{\"CLIPSORT\":\"wrong\"},\"micSendEnabled\":true}";
        fs::write(directory.path().join("settings.json"), source).unwrap();
        let mut store = Store::open(directory.path().into()).unwrap();
        assert_eq!(store.settings().last_preset_id.as_deref(), Some("night"));
        assert_eq!(store.settings().clip_hotkeys.keys().map(String::as_str).collect::<Vec<_>>(), ["clip-b", "clip-a"]);
        assert_eq!(store.settings().global_hotkeys.get("unknownAction"), Some(&Some("Ctrl+M".into())));
        assert_eq!(store.settings().ui.clip_sort, "name");
        assert_eq!(store.settings_issues_shared()[0].key, "clipSort");
        store.save_settings(store.settings().clone()).unwrap();
        let written = read_json(&directory.path().join("settings.json")).unwrap();
        assert!(written.get("micSendEnabled").is_none());
        assert_eq!(written["globalHotkeys"]["unknownAction"], "Ctrl+M");
        assert_eq!(fs::read_to_string(directory.path().join("settings.json.bak")).unwrap(), source);
    }

    #[test]
    fn library_order_setting_survives_save_and_reopen() {
        let directory = tempdir().unwrap();
        let mut store = Store::open(directory.path().into()).unwrap();
        let mut settings = store.settings().clone();
        settings.ui.clip_sort = "libraryOrder".into();
        store.save_settings(settings).unwrap();
        assert!(store.settings_issues_shared().is_empty());
        let reopened = Store::open(directory.path().into()).unwrap();
        assert_eq!(reopened.settings().ui.clip_sort, "libraryOrder");
        assert!(reopened.settings_issues_shared().is_empty());
    }

    #[test]
    fn preset_backup_restore_preserves_full_config_and_previous_main() {
        let directory = tempdir().unwrap();
        let store = Store::open(directory.path().into()).unwrap();
        let original = preset("night", "夜间开黑");
        store.save_preset(&original).unwrap();
        let mut updated = original.clone(); updated.name = "新名称".into(); updated.mic_gain = 3.0;
        store.save_preset(&updated).unwrap();
        store.restore_preset_backup("night").unwrap();
        assert_eq!(store.load_preset("night").unwrap(), original);
        let backup = read_json(&directory.path().join("presets/night.json.bak")).unwrap();
        assert_eq!(backup["name"], "新名称");
        assert_eq!(backup["microphone"]["gain"], 3.0);
        let disk = read_json(&directory.path().join("presets/night.json")).unwrap();
        assert_eq!(disk["application"]["selection"]["executablePath"], "C:\\Apps\\音乐.exe");
        assert_eq!(disk["virtualOutput"]["captureDeviceStableId"], "capture-id");
    }

    #[test]
    fn rejected_gain_and_corrupt_backup_leave_existing_files_untouched() {
        let directory = tempdir().unwrap();
        let store = Store::open(directory.path().into()).unwrap();
        let mut config = preset("night", "夜间");
        store.save_preset(&config).unwrap();
        let path = directory.path().join("presets/night.json");
        let before = fs::read(&path).unwrap();
        config.clip_gain = 4.01;
        assert_eq!(store.save_preset(&config).unwrap_err().code, "gain-out-of-range");
        assert_eq!(fs::read(&path).unwrap(), before);
        let backup = directory.path().join("presets/night.json.bak");
        fs::write(&backup, b"{\"schemaVersion\":2}").unwrap();
        let backup_before = fs::read(&backup).unwrap();
        assert_eq!(store.restore_preset_backup("night").unwrap_err().code, "unsupported-schema");
        assert_eq!(fs::read(&path).unwrap(), before);
        assert_eq!(fs::read(&backup).unwrap(), backup_before);
        assert_eq!(store.save_preset(&preset("../escape", "bad")).unwrap_err().code, "invalid-preset-id");
    }

    #[test]
    fn corrupt_settings_are_reported_without_repair_and_corrupt_presets_stay_visible() {
        let directory = tempdir().unwrap();
        let path = directory.path().join("settings.json");
        fs::write(&path, b"{\"schemaVersion\":9}").unwrap();
        fs::create_dir(directory.path().join("presets")).unwrap();
        fs::write(directory.path().join("presets/broken.json"), b"not json").unwrap();
        let before = fs::read(&path).unwrap();
        let store = Store::open(directory.path().into()).unwrap();
        assert_eq!(store.settings_error_shared().unwrap().code, "unsupported-schema");
        assert_eq!(store.settings().last_preset_id, None);
        assert_eq!(fs::read(path).unwrap(), before);
        let presets = store.list_presets().unwrap();
        assert_eq!(presets[0].preset_id, "broken");
        assert!(presets[0].corrupt);
    }
}
