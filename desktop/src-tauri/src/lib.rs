mod broker;
mod models;
mod runtime;
mod store;

use std::sync::Arc;
use tauri::{Emitter, Manager};
use models::AppResult;
use runtime::{DesktopRuntime, DesktopView};

#[tauri::command]
async fn desktop_state(state: tauri::State<'_, Arc<DesktopRuntime>>) -> AppResult<DesktopView> {
    state.snapshot().await
}

#[tauri::command]
async fn desktop_command(op: String, args: serde_json::Value, state: tauri::State<'_, Arc<DesktopRuntime>>) -> AppResult<serde_json::Value> {
    state.command(&op, args).await
}

pub fn run() {
    let app = tauri::Builder::default()
        .plugin(tauri_plugin_single_instance::init(|app, _args, _cwd| {
            if let Some(window) = app.get_webview_window("main") {
                if let Err(error) = window.show().and_then(|_| window.set_focus()) {
                    let _ = app.emit("engine_notice", serde_json::json!({"kind":"Info","message":error.to_string()}));
                }
            }
        }))
        .plugin(tauri_plugin_shell::init())
        .plugin(tauri_plugin_dialog::init())
        .plugin(tauri_plugin_opener::init())
        .invoke_handler(tauri::generate_handler![desktop_state, desktop_command])
        .setup(|app| {
            let runtime = Arc::new(DesktopRuntime::new(app.handle().clone())?);
            app.manage(runtime.clone());
            let configuration = app.config().app.windows.iter().find(|window| window.label == "main")
                .ok_or_else(|| std::io::Error::other("Missing main window configuration"))?;
            tauri::WebviewWindowBuilder::from_config(app.handle(), configuration)?
                .data_directory(runtime.data_root().join("webview"))
                .build()?;
            runtime.install_tray()?;
            let startup = runtime.clone();
            tauri::async_runtime::spawn(async move {
                if let Err(error) = startup.start_engine().await {
                    startup.report_error(error);
                }
            });
            let handle = runtime.clone();
            if let Some(window) = app.get_webview_window("main") {
                window.on_window_event(move |event| {
                    if let tauri::WindowEvent::CloseRequested { api, .. } = event {
                        if !handle.is_exiting() {
                            api.prevent_close();
                            handle.request_close();
                        }
                    }
                });
            }
            Ok(())
        })
        .build(tauri::generate_context!())
        .expect("Unable to create the native OnbodyMic application");

    app.run(|app, event| {
        if let tauri::RunEvent::ExitRequested { api, .. } = event {
            let runtime = app.state::<Arc<DesktopRuntime>>();
            if !runtime.is_exiting() {
                api.prevent_exit();
                runtime.request_exit();
            }
        }
    });
}
