use std::collections::HashMap;
use std::path::Path;
use std::sync::{Arc, Mutex};
use std::sync::atomic::{AtomicU64, Ordering};
use serde::{Deserialize, Serialize};
use serde_json::Value;
use tauri::{AppHandle, Manager};
use tauri_plugin_shell::{ShellExt, process::{CommandChild, CommandEvent}};
use tokio::sync::oneshot;
use crate::models::{AppResult, CommandError};
use crate::runtime::DesktopRuntime;

#[derive(Deserialize)]
struct WireError { code: String, message: String }
#[derive(Deserialize)]
struct WireMessage {
    id: Option<u64>,
    ok: Option<bool>,
    result: Option<Value>,
    error: Option<WireError>,
    event: Option<String>,
    payload: Option<Value>,
}
#[derive(Serialize)]
struct Request<'a> { id: u64, op: &'a str, args: &'a Value }

pub struct EngineBroker {
    generation: u64,
    process: Mutex<Option<CommandChild>>,
    pending: Mutex<HashMap<u64, oneshot::Sender<AppResult<Value>>>>,
    next_id: AtomicU64,
    last_error: Mutex<Option<String>>,
}

impl EngineBroker {
    pub fn spawn(app: &AppHandle, root: &Path, generation: u64) -> AppResult<Arc<Self>> {
        let extract = root.join("runtime-cache");
        std::fs::create_dir_all(&extract).map_err(|error| CommandError::new("io-error", error.to_string()))?;
        let command = app.shell().sidecar("OnbodyMic.EngineHost")
            .map_err(|error| CommandError::new("backend-unavailable", error.to_string()))?
            .args(["--root", root.to_string_lossy().as_ref()])
            .env("DOTNET_BUNDLE_EXTRACT_BASE_DIR", extract)
            .set_process_group(true)
            .kill_on_drop(true);
        let (mut receiver, child) = command.spawn().map_err(|error| CommandError::new("backend-unavailable", error.to_string()))?;
        let broker = Arc::new(Self { generation, process: Mutex::new(Some(child)), pending: Mutex::new(HashMap::new()), next_id: AtomicU64::new(1), last_error: Mutex::new(None) });
        let reading = broker.clone();
        let app = app.clone();
        tauri::async_runtime::spawn(async move {
            while let Some(event) = receiver.recv().await {
                match event {
                    CommandEvent::Stdout(bytes) => {
                        match serde_json::from_slice::<WireMessage>(&bytes) {
                            Ok(message) => {
                                if let Some(id) = message.id {
                                    if let Some(waiter) = reading.pending.lock().expect("pending requests poisoned").remove(&id) {
                                        let result = if message.ok == Some(true) {
                                            Ok(message.result.unwrap_or(Value::Null))
                                        } else {
                                            let error = message.error;
                                            Err(match error {
                                                Some(error) => CommandError::new(match error.code.as_str() { "rejected" => "rejected", "invalid-argument" => "invalid-argument", _ => "backend-error" }, error.message),
                                                None => CommandError::new("protocol-error", "音频后端响应缺少错误信息"),
                                            })
                                        };
                                        let _ = waiter.send(result);
                                    }
                                } else if let Some(event) = message.event {
                                    if let Some(payload) = message.payload {
                                        app.state::<Arc<DesktopRuntime>>().handle_engine_event(reading.generation, &event, payload);
                                    }
                                }
                            }
                            Err(error) => {
                                *reading.last_error.lock().expect("last error poisoned") = Some(format!("音频协议响应无效: {error}"));
                                reading.terminate();
                                break;
                            }
                        }
                    }
                    CommandEvent::Stderr(bytes) => {
                        let message = String::from_utf8_lossy(&bytes).into_owned();
                        *reading.last_error.lock().expect("last error poisoned") = Some(message.clone());
                        app.state::<Arc<DesktopRuntime>>().notice("Info", message);
                    }
                    CommandEvent::Error(error) => {
                        *reading.last_error.lock().expect("last error poisoned") = Some(error);
                        break;
                    }
                    CommandEvent::Terminated(_) => break,
                    _ => (),
                }
            }
            reading.process.lock().expect("process handle poisoned").take();
            let message = reading.last_error.lock().expect("last error poisoned").take().unwrap_or_else(|| "音频后端已停止".into());
            reading.fail_pending(&message);
            app.state::<Arc<DesktopRuntime>>().handle_engine_stopped(reading.generation, message);
        });
        Ok(broker)
    }

    pub async fn request(&self, op: &str, args: Value) -> AppResult<Value> {
        let id = self.next_id.fetch_add(1, Ordering::Relaxed);
        let (sender, receiver) = oneshot::channel();
        let mut bytes = serde_json::to_vec(&Request { id, op, args: &args }).map_err(|error| CommandError::new("protocol-error", error.to_string()))?;
        bytes.push(b'\n');
        self.pending.lock().expect("pending requests poisoned").insert(id, sender);
        let sent = {
            let mut process = self.process.lock().expect("process handle poisoned");
            match process.as_mut() {
                Some(process) => process.write(&bytes).map_err(|error| CommandError::new("backend-unavailable", error.to_string())),
                None => Err(CommandError::new("backend-unavailable", "音频后端尚未运行")),
            }
        };
        if let Err(error) = sent {
            self.pending.lock().expect("pending requests poisoned").remove(&id);
            return Err(error);
        }
        receiver.await.map_err(|_| CommandError::new("backend-unavailable", "音频后端响应已中断"))?
    }

    pub fn terminate(&self) {
        if let Some(process) = self.process.lock().expect("process handle poisoned").take() {
            if let Err(error) = process.kill() {
                *self.last_error.lock().expect("last error poisoned") = Some(error.to_string());
            }
        }
        self.fail_pending("音频后端已停止");
    }

    fn fail_pending(&self, message: &str) {
        let pending = std::mem::take(&mut *self.pending.lock().expect("pending requests poisoned"));
        for (_, waiter) in pending {
            let _ = waiter.send(Err(CommandError::new("backend-unavailable", message.to_owned())));
        }
    }
}
