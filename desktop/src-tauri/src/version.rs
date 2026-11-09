use serde::Serialize;

pub const VERSION: &str = env!("CARGO_PKG_VERSION");
pub const COMMIT: &str = env!("ONBODYMIC_GIT_COMMIT");
pub const COMMIT_SHORT: &str = env!("ONBODYMIC_GIT_COMMIT_SHORT");
pub const COMMIT_TIME: &str = env!("ONBODYMIC_GIT_COMMIT_TIME");
pub const BUILD_TIME: &str = env!("ONBODYMIC_BUILD_TIME");
pub const REPOSITORY: &str = "https://github.com/Benincasamatch/onbody_mic";
pub const RELEASES_URL: &str = "https://github.com/Benincasamatch/onbody_mic/releases";
pub const UPDATE_API: &str = "https://api.github.com/repos/Benincasamatch/onbody_mic/releases/latest";
const CHANGELOG: &str = include_str!("../../../CHANGELOG.md");

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct BuildInfo {
    pub version: &'static str,
    pub commit: &'static str,
    pub commit_short: &'static str,
    pub commit_time: &'static str,
    pub build_time: &'static str,
    pub dirty: bool,
}

#[derive(Clone, Debug, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ReleaseSection {
    pub title: String,
    pub items: Vec<String>,
}

#[derive(Clone, Debug, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ReleaseNote {
    pub version: String,
    pub date: Option<String>,
    pub sections: Vec<ReleaseSection>,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AppInfo {
    pub build: BuildInfo,
    pub releases: Vec<ReleaseNote>,
    pub install_kind: &'static str,
    pub repository: &'static str,
    pub releases_url: &'static str,
    pub update_api: &'static str,
}

pub fn build_info() -> BuildInfo {
    BuildInfo {
        version: VERSION,
        commit: COMMIT,
        commit_short: COMMIT_SHORT,
        commit_time: COMMIT_TIME,
        build_time: BUILD_TIME,
        dirty: matches!(env!("ONBODYMIC_GIT_DIRTY"), "1" | "true" | "yes"),
    }
}

pub fn app_info() -> AppInfo {
    AppInfo {
        build: build_info(),
        releases: parse_changelog(CHANGELOG).into_iter().take(12).collect(),
        install_kind: crate::update::install_kind(),
        repository: REPOSITORY,
        releases_url: RELEASES_URL,
        update_api: UPDATE_API,
    }
}

/// Minimal parser for the Keep a Changelog subset used by this repository.
/// HTML and nested lists are not interpreted; the UI renders the resulting text.
pub fn parse_changelog(markdown: &str) -> Vec<ReleaseNote> {
    let mut releases = Vec::new();
    let mut release: Option<ReleaseNote> = None;
    let mut section: Option<ReleaseSection> = None;

    for raw in markdown.lines() {
        let line = raw.trim_end();
        if let Some(heading) = line.strip_prefix("## ") {
            flush(&mut release, &mut section, &mut releases);
            if let Some((version, date)) = parse_release_heading(heading) {
                release = Some(ReleaseNote { version, date, sections: Vec::new() });
            }
            continue;
        }
        if let Some(heading) = line.strip_prefix("### ") {
            flush_section(&mut release, &mut section);
            section = Some(ReleaseSection { title: heading.trim().to_owned(), items: Vec::new() });
            continue;
        }
        if release.is_none() { continue; }
        if let Some(item) = line.strip_prefix("- ").or_else(|| line.strip_prefix("* ")) {
            let current_section = section.get_or_insert_with(|| ReleaseSection { title: "变更".to_owned(), items: Vec::new() });
            current_section.items.push(item.trim().to_owned());
            continue;
        }
        let Some(current_release) = release.as_mut() else { continue };
        let Some(current_section) = section.as_mut() else { continue };
        if !line.trim().is_empty() && line.starts_with("  ") {
            if let Some(last) = current_section.items.last_mut() {
                last.push(' ');
                last.push_str(line.trim());
            }
        } else if line.trim().is_empty() {
            // Blank lines separate blocks; the next heading flushes the current one.
        } else if current_section.items.is_empty() && current_release.sections.is_empty() {
            // A section may start with a prose paragraph; keep it as one item.
            current_section.items.push(line.trim().to_owned());
        }
    }
    flush(&mut release, &mut section, &mut releases);
    releases
}

fn parse_release_heading(value: &str) -> Option<(String, Option<String>)> {
    let value = value.trim();
    let value = value.strip_prefix('[').unwrap_or(value);
    let (version, remainder) = match value.split_once(']') {
        Some((version, remainder)) => (version, remainder),
        None => match value.split_once(" - ") {
            Some((version, remainder)) => (version, remainder),
            None => (value, ""),
        },
    };
    let version = version.trim().trim_end_matches(']').trim();
    if version.is_empty() { return None; }
    let date = remainder.trim().strip_prefix('-').map(str::trim).filter(|date| !date.is_empty());
    Some((version.to_owned(), date.map(str::to_owned)))
}

fn flush_section(release: &mut Option<ReleaseNote>, section: &mut Option<ReleaseSection>) {
    if let Some(finished) = section.take() {
        if !finished.items.is_empty() {
            if let Some(release) = release.as_mut() {
                release.sections.push(finished);
            }
        }
    }
}

fn flush(release: &mut Option<ReleaseNote>, section: &mut Option<ReleaseSection>, releases: &mut Vec<ReleaseNote>) {
    flush_section(release, section);
    if let Some(finished) = release.take() {
        releases.push(finished);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn changelog_parses_releases_sections_and_dates() {
        let notes = parse_changelog("# 变更记录\n\nIntro\n\n## [1.2.3] - 2026-11-02\n\n### 新增\n\n- 第一项\n- 第二项\n\n### 修复\n\n- 第三项\n\n## [1.2.2] - 2026-10-01\n\n### 变更\n\n- 旧项\n");
        assert_eq!(notes.len(), 2);
        assert_eq!(notes[0].version, "1.2.3");
        assert_eq!(notes[0].date.as_deref(), Some("2026-11-02"));
        assert_eq!(notes[0].sections.len(), 2);
        assert_eq!(notes[0].sections[0].title, "新增");
        assert_eq!(notes[0].sections[0].items, ["第一项", "第二项"]);
        assert_eq!(notes[1].sections[0].items, ["旧项"]);
    }

    #[test]
    fn changelog_joins_wrapped_bullets_and_skips_empty_sections() {
        let notes = parse_changelog("## 2.0.0\n### 变更\n- 第一行\n  续行\n### 空节\n## 1.0.0\n- 无节条目\n");
        assert_eq!(notes.len(), 2);
        assert_eq!(notes[0].sections.len(), 1);
        assert_eq!(notes[0].sections[0].items, ["第一行 续行"]);
        assert_eq!(notes[1].sections[0].title, "变更");
        assert_eq!(notes[1].sections[0].items, ["无节条目"]);
    }

    #[test]
    fn embedded_changelog_is_parsed() {
        let notes = parse_changelog(CHANGELOG);
        assert!(notes.len() >= 5);
        assert!(notes.iter().all(|note| !note.sections.is_empty()));
        assert!(notes.iter().any(|note| note.version == VERSION));
    }

    #[test]
    fn build_info_has_version_and_stamp_fields() {
        let info = build_info();
        assert_eq!(info.version, VERSION);
        assert!(!info.commit_short.is_empty());
        assert!(!info.build_time.is_empty());
    }
}
