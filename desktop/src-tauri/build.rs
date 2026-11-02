use std::process::Command;
use std::time::{SystemTime, UNIX_EPOCH};

fn main() {
    tauri_build::build();

    // Keep the stamp fresh when the checkout moves. Missing .git (CI archives) is fine.
    for path in ["../../.git/HEAD", "../../.git/index"] {
        println!("cargo:rerun-if-changed={path}");
    }

    let commit = override_or("ONBODYMIC_GIT_COMMIT", || git(&["rev-parse", "HEAD"]))
        .unwrap_or_else(|| "unknown".to_owned());
    let commit_short = if commit == "unknown" { commit.clone() } else { commit.chars().take(12).collect() };
    let commit_time = override_or("ONBODYMIC_GIT_COMMIT_TIME", || git(&["show", "-s", "--format=%cI", "HEAD"]))
        .unwrap_or_else(|| "unknown".to_owned());
    let dirty = match std::env::var("ONBODYMIC_GIT_DIRTY") {
        Ok(value) => matches!(value.trim().to_ascii_lowercase().as_str(), "1" | "true" | "yes"),
        Err(_) => git(&["status", "--porcelain", "--untracked-files=no"]).is_some_and(|text| !text.trim().is_empty()),
    };
    let build_time = std::env::var("ONBODYMIC_BUILD_TIME")
        .ok()
        .filter(|value| !value.trim().is_empty())
        .or_else(|| std::env::var("SOURCE_DATE_EPOCH").ok().and_then(|value| value.trim().parse::<u64>().ok()).map(format_utc))
        .unwrap_or_else(now_utc);

    println!("cargo:rustc-env=ONBODYMIC_GIT_COMMIT={commit}");
    println!("cargo:rustc-env=ONBODYMIC_GIT_COMMIT_SHORT={commit_short}");
    println!("cargo:rustc-env=ONBODYMIC_GIT_COMMIT_TIME={commit_time}");
    println!("cargo:rustc-env=ONBODYMIC_GIT_DIRTY={}", if dirty { "1" } else { "0" });
    println!("cargo:rustc-env=ONBODYMIC_BUILD_TIME={build_time}");
}

fn override_or(name: &str, fallback: impl FnOnce() -> Option<String>) -> Option<String> {
    std::env::var(name).ok().filter(|value| !value.trim().is_empty()).or_else(fallback)
}

fn git(arguments: &[&str]) -> Option<String> {
    let output = Command::new("git").args(arguments).output().ok()?;
    if !output.status.success() { return None; }
    String::from_utf8(output.stdout).ok().map(|value| value.trim().to_owned()).filter(|value| !value.is_empty())
}

fn now_utc() -> String {
    let seconds = SystemTime::now().duration_since(UNIX_EPOCH).map(|duration| duration.as_secs()).unwrap_or(0);
    format_utc(seconds)
}

/// ISO 8601 UTC without pulling a date/time dependency into the build script.
fn format_utc(seconds: u64) -> String {
    let days = (seconds / 86_400) as i64;
    let remainder = seconds % 86_400;
    let (year, month, day) = civil_from_days(days);
    format!("{year:04}-{month:02}-{day:02}T{:02}:{:02}:{:02}Z", remainder / 3_600, remainder % 3_600 / 60, remainder % 60)
}

/// Howard Hinnant's days-to-civil algorithm; valid for the Unix epoch range used here.
fn civil_from_days(days: i64) -> (i64, u32, u32) {
    let days = days + 719_468;
    let era = if days >= 0 { days } else { days - 146_096 } / 146_097;
    let day_of_era = days - era * 146_097;
    let year_of_era = (day_of_era - day_of_era / 1_460 + day_of_era / 36_524 - day_of_era / 146_096) / 365;
    let year = year_of_era + era * 400;
    let day_of_year = day_of_era - (365 * year_of_era + year_of_era / 4 - year_of_era / 100);
    let month_index = (5 * day_of_year + 2) / 153;
    let day = day_of_year - (153 * month_index + 2) / 5 + 1;
    let month = if month_index < 10 { month_index + 3 } else { month_index - 9 };
    (if month <= 2 { year + 1 } else { year }, month as u32, day as u32)
}
