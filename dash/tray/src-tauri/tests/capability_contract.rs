//! Contract for the tray webview's least-privilege capability manifest.

use std::fs;
use std::path::PathBuf;

use serde_json::Value;

const CUSTOM_COMMAND_PERMISSIONS: [&str; 6] = [
    "allow-get-view-state",
    "allow-refresh-now",
    "allow-open-view",
    "allow-set-settings",
    "allow-quit",
    "allow-hide-popover",
];

const EVENT_LISTENING_PERMISSIONS: [&str; 2] =
    ["core:event:allow-listen", "core:event:allow-unlisten"];

const FORBIDDEN_EVENT_PERMISSIONS: [&str; 3] = [
    "core:event:default",
    "core:event:allow-emit",
    "core:event:allow-emit-to",
];

fn capability_manifest_path() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("capabilities/tray.json")
}

fn permissions() -> Vec<String> {
    let path = capability_manifest_path();
    let contents = fs::read_to_string(&path)
        .unwrap_or_else(|error| panic!("read capability manifest {}: {error}", path.display()));
    let manifest: Value = serde_json::from_str(&contents)
        .unwrap_or_else(|error| panic!("parse capability manifest {}: {error}", path.display()));

    manifest
        .get("permissions")
        .and_then(Value::as_array)
        .unwrap_or_else(|| {
            panic!(
                "capability manifest {} has no permissions array",
                path.display()
            )
        })
        .iter()
        .map(|permission| {
            permission
                .as_str()
                .unwrap_or_else(|| {
                    panic!("capability manifest contains a non-string permission: {permission}")
                })
                .to_owned()
        })
        .collect()
}

#[test]
fn tray_capability_grants_event_listening_without_webview_emit() {
    let granted = permissions();

    for permission in CUSTOM_COMMAND_PERMISSIONS {
        assert!(
            granted.iter().any(|granted| granted == permission),
            "missing existing custom command permission: {permission}"
        );
    }

    let missing_event_permissions: Vec<&str> = EVENT_LISTENING_PERMISSIONS
        .into_iter()
        .filter(|permission| !granted.iter().any(|granted| granted == permission))
        .collect();
    assert!(
        missing_event_permissions.is_empty(),
        "missing event-listening permissions: {}",
        missing_event_permissions.join(", ")
    );

    for permission in FORBIDDEN_EVENT_PERMISSIONS {
        assert!(
            granted.iter().all(|granted| granted != permission),
            "forbidden event permission granted: {permission}"
        );
    }
}
