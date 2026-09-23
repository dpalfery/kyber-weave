//! RED contract for the macOS LaunchAgent lifecycle policy.
//!
//! The login item must start the tray at login and ask launchd to restart it
//! only after an unsuccessful exit.  A clean tray quit must remain stopped.

#![cfg(target_os = "macos")]

use std::path::Path;

use kyberdash_tray_lib::login_item_macos::{plist_for, LABEL};

const PROGRAM: &str = "/Applications/KyberDash.app/Contents/MacOS/kyberdash-tray";

#[test]
fn launch_agent_restarts_only_after_an_unsuccessful_exit() {
    let plist = plist_for(Path::new(PROGRAM));

    assert!(
        plist.contains(&format!("<key>Label</key>\n    <string>{LABEL}</string>")),
        "LaunchAgent label must remain the bundle identifier: {plist}"
    );
    assert!(
        plist.contains("<key>ProgramArguments</key>\n    <array>\n        <string>/Applications/KyberDash.app/Contents/MacOS/kyberdash-tray</string>"),
        "LaunchAgent must execute the installed tray with an absolute path: {plist}"
    );
    assert!(
        plist.contains("<key>RunAtLoad</key>\n    <true/>"),
        "LaunchAgent must start the tray at login: {plist}"
    );
    assert!(
        !plist.contains("<key>KeepAlive</key>\n    <true/>"),
        "KeepAlive must not be an unconditional restart policy: {plist}"
    );

    let keep_alive = plist.find("<key>KeepAlive</key>").unwrap_or_else(|| {
        panic!(
            "LaunchAgent must include a crash-only KeepAlive dictionary with SuccessfulExit=false: {plist}"
        )
    });
    let keep_alive_policy = &plist[keep_alive..];
    assert!(
        keep_alive_policy.contains("<key>KeepAlive</key>\n    <dict>"),
        "KeepAlive must be a dictionary, not a boolean restart policy: {plist}"
    );
    assert!(
        keep_alive_policy.contains("<key>SuccessfulExit</key>\n        <false/>"),
        "a clean Quit must remain stopped while unsuccessful exits may restart: {plist}"
    );
}
