use std::borrow::Cow;
use std::fmt;
use indexmap::IndexMap;
use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct CommandError {
    pub code: Cow<'static, str>,
    pub message: Cow<'static, str>,
}

impl CommandError {
    pub fn new(code: &'static str, message: impl Into<Cow<'static, str>>) -> Self {
        Self { code: Cow::Borrowed(code), message: message.into() }
    }
}
impl fmt::Display for CommandError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(formatter, "{}: {}", self.code, self.message)
    }
}
impl std::error::Error for CommandError {}
pub type AppResult<T> = Result<T, CommandError>;

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(default, rename_all = "camelCase")]
pub struct UiPreferencesData {
    pub start_minimized: bool,
    pub close_to_tray_acknowledged: bool,
    pub clip_sort: String,
    pub reduce_transparency: bool,
}
impl Default for UiPreferencesData {
    fn default() -> Self {
        Self { start_minimized: false, close_to_tray_acknowledged: false, clip_sort: "name".into(), reduce_transparency: false }
    }
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(default, rename_all = "camelCase")]
pub struct SettingsData {
    pub schema_version: u32,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub last_preset_id: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub library_root: Option<String>,
    pub global_hotkeys: IndexMap<String, Option<String>>,
    pub clip_hotkeys: IndexMap<String, Option<String>>,
    pub preset_hotkeys: IndexMap<String, Option<String>>,
    pub ui: UiPreferencesData,
}
impl Default for SettingsData {
    fn default() -> Self {
        Self {
            schema_version: 1, last_preset_id: None, library_root: None,
            global_hotkeys: IndexMap::new(), clip_hotkeys: IndexMap::new(), preset_hotkeys: IndexMap::new(),
            ui: UiPreferencesData::default(),
        }
    }
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct AppSelection {
    pub executable_path: Option<String>,
    pub display_name: Option<String>,
}
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct DeviceConfig {
    pub mic_device_id: String,
    pub virtual_render_device_id: String,
    pub virtual_capture_device_id: Option<String>,
    pub monitor_device_id: String,
    pub application: Option<AppSelection>,
}
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct MonitorPreferences {
    pub mode: String,
    pub gain: f32,
}
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct PresetConfig {
    pub preset_id: String,
    pub name: String,
    pub devices: DeviceConfig,
    pub mic_gain: f32,
    pub app_gain: f32,
    pub clip_gain: f32,
    pub monitor: MonitorPreferences,
    pub clip_layout: Vec<String>,
    pub library_favorites_only: bool,
    pub library_group_id: Option<String>,
}
#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PresetSummary {
    pub preset_id: String,
    pub name: String,
    pub corrupt: bool,
    pub reason: Option<String>,
}
#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SettingsIssue {
    pub section: String,
    pub key: String,
    pub reason: String,
}
