//! Launch at login on macOS, as a LaunchAgent.
//!
//! Requirement 6.8 registers the tray to start at login when the user asks, and
//! defaults the setting to off. Windows has the `Run` key in the salvaged
//! `autostart.rs`; this is the macOS half, which that module's stub was waiting
//! for.
//!
//! A LaunchAgent rather than a Login Item: a Login Item is registered through
//! `SMAppService`, which needs the app to be in `/Applications` and signed, and
//! `kyberdash menubar` installs into the user's own tree. A plist in
//! `~/Library/LaunchAgents` is also something the user can read and delete
//! without a tool, which a Login Item is not.
//!
//! The agent directory is a parameter so the writer is tested against a
//! temporary directory rather than the developer's own login items.

use std::path::{Path, PathBuf};

use anyhow::{Context, Result};

/// The bundle identifier of Requirement 3.6, which is also the agent's label
/// and the plist's file name.
pub const LABEL: &str = "io.github.dpalfery.kyberdash";

/// `~/Library/LaunchAgents`, where a per-user agent lives.
pub fn agent_dir(home: &Path) -> PathBuf {
    home.join("Library").join("LaunchAgents")
}

pub fn plist_path(agent_dir: &Path) -> PathBuf {
    agent_dir.join(format!("{LABEL}.plist"))
}

/// True when the agent is registered.
pub fn is_enabled(agent_dir: &Path) -> bool {
    plist_path(agent_dir).is_file()
}

/// Writes or removes the agent.
///
/// Writing is atomic — a temporary file then a rename — because `launchd` reads
/// this directory on its own schedule, and a half-written plist at login is a
/// startup that silently does not happen.
pub fn set_enabled(agent_dir: &Path, enabled: bool, program: &Path) -> Result<()> {
    let path = plist_path(agent_dir);
    if !enabled {
        match std::fs::remove_file(&path) {
            Ok(()) => return Ok(()),
            // Already absent is the requested state, not a failure.
            Err(err) if err.kind() == std::io::ErrorKind::NotFound => return Ok(()),
            Err(err) => return Err(err).context("removing the LaunchAgent"),
        }
    }

    std::fs::create_dir_all(agent_dir).context("creating ~/Library/LaunchAgents")?;
    let temporary = path.with_extension("plist.tmp");
    std::fs::write(&temporary, plist_for(program)).context("writing the LaunchAgent")?;
    std::fs::rename(&temporary, &path).context("installing the LaunchAgent")?;
    Ok(())
}

/// The agent's plist.
///
/// `RunAtLoad` and nothing else: no `KeepAlive`, because a tray the user quit
/// must stay quit until the next login rather than being restarted under them.
pub fn plist_for(program: &Path) -> String {
    format!(
        r#"<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key>
    <string>{label}</string>
    <key>ProgramArguments</key>
    <array>
        <string>{program}</string>
    </array>
    <key>RunAtLoad</key>
    <true/>
    <key>ProcessType</key>
    <string>Interactive</string>
</dict>
</plist>
"#,
        label = LABEL,
        program = escape_xml(&program.to_string_lossy()),
    )
}

/// A path is user data — it holds whatever they named their directories — and
/// this one is interpolated into XML.
fn escape_xml(value: &str) -> String {
    value
        .replace('&', "&amp;")
        .replace('<', "&lt;")
        .replace('>', "&gt;")
        .replace('"', "&quot;")
        .replace('\'', "&apos;")
}

#[cfg(test)]
mod tests {
    use super::*;

    struct Scratch {
        root: PathBuf,
    }

    impl Scratch {
        fn new(tag: &str) -> Self {
            let root = std::env::temp_dir().join(format!(
                "kyberdash-login-{tag}-{}-{:?}",
                std::process::id(),
                std::time::SystemTime::now()
                    .duration_since(std::time::UNIX_EPOCH)
                    .unwrap()
                    .as_nanos()
            ));
            std::fs::create_dir_all(&root).unwrap();
            Scratch { root }
        }
    }

    impl Drop for Scratch {
        fn drop(&mut self) {
            std::fs::remove_dir_all(&self.root).ok();
        }
    }

    fn program() -> PathBuf {
        PathBuf::from("/Applications/KyberDash.app/Contents/MacOS/kyberdash-tray")
    }

    /// Requirement 3.6's identifier is the label and the file name, because
    /// `launchctl` and the user both find the agent by it.
    #[test]
    fn the_agent_is_named_for_the_bundle_identifier() {
        let scratch = Scratch::new("naming");
        let dir = agent_dir(&scratch.root);

        assert_eq!(dir, scratch.root.join("Library").join("LaunchAgents"));
        assert_eq!(
            plist_path(&dir).file_name().unwrap(),
            "io.github.dpalfery.kyberdash.plist"
        );
    }

    /// Requirement 6.8: off until asked, on when asked, off again when unasked.
    #[test]
    fn enabling_writes_the_agent_and_disabling_removes_it() {
        let scratch = Scratch::new("toggle");
        let dir = agent_dir(&scratch.root);

        assert!(!is_enabled(&dir), "nothing is registered by default");

        set_enabled(&dir, true, &program()).unwrap();
        assert!(is_enabled(&dir));

        set_enabled(&dir, false, &program()).unwrap();
        assert!(!is_enabled(&dir));
    }

    /// Disabling something already disabled is the requested state, not an error.
    #[test]
    fn disabling_when_absent_succeeds() {
        let scratch = Scratch::new("absent");
        let dir = agent_dir(&scratch.root);

        assert!(set_enabled(&dir, false, &program()).is_ok());
        assert!(set_enabled(&dir, false, &program()).is_ok());
    }

    /// Enabling twice must not leave a stale temporary file behind.
    #[test]
    fn enabling_twice_leaves_one_plist_and_no_temporary() {
        let scratch = Scratch::new("twice");
        let dir = agent_dir(&scratch.root);

        set_enabled(&dir, true, &program()).unwrap();
        set_enabled(&dir, true, &program()).unwrap();

        let entries: Vec<String> = std::fs::read_dir(&dir)
            .unwrap()
            .map(|e| e.unwrap().file_name().to_string_lossy().into_owned())
            .collect();
        assert_eq!(entries, vec!["io.github.dpalfery.kyberdash.plist"]);
    }

    #[test]
    fn the_plist_starts_the_program_at_load() {
        let scratch = Scratch::new("contents");
        let dir = agent_dir(&scratch.root);
        set_enabled(&dir, true, &program()).unwrap();

        let written = std::fs::read_to_string(plist_path(&dir)).unwrap();
        assert!(written.contains("<key>Label</key>"));
        assert!(written.contains("io.github.dpalfery.kyberdash"));
        assert!(written.contains("<key>RunAtLoad</key>"));
        assert!(written.contains("<true/>"));
        assert!(written.contains("/Applications/KyberDash.app/Contents/MacOS/kyberdash-tray"));
    }

    /// A tray the user quit stays quit until the next login; `KeepAlive` would
    /// restart it under them.
    #[test]
    fn the_plist_does_not_keep_the_tray_alive() {
        assert!(!plist_for(&program()).contains("KeepAlive"));
    }

    /// The program path holds whatever the user named their directories, and it
    /// is interpolated into XML.
    #[test]
    fn a_path_with_xml_characters_is_escaped() {
        let awkward = PathBuf::from("/Users/a&b/<Apps>/\"KyberDash\".app/Contents/MacOS/tray");
        let plist = plist_for(&awkward);

        assert!(plist.contains("a&amp;b"), "{plist}");
        assert!(plist.contains("&lt;Apps&gt;"), "{plist}");
        assert!(plist.contains("&quot;KyberDash&quot;"), "{plist}");
        // The raw characters must not survive into the document.
        assert!(!plist.contains("a&b"));
        assert!(!plist.contains("<Apps>"));
    }

    /// The directory may not exist on a machine that has never had an agent.
    #[test]
    fn enabling_creates_the_launch_agents_directory() {
        let scratch = Scratch::new("mkdir");
        let dir = agent_dir(&scratch.root);
        assert!(!dir.exists());

        set_enabled(&dir, true, &program()).unwrap();
        assert!(dir.is_dir());
    }
}
