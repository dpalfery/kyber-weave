// Stops an extra console window appearing on Windows in release builds.
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

fn main() {
    codeburn_menubar_lib::run()
}
