/// Launch-argument handling for the single-instance plugin (design D7).
///
/// `--quit` is forwarded to the running instance so an installer can stop the
/// process tree without a pidfile, which cannot reap children on Windows.
pub fn wants_quit<I, S>(args: I) -> bool
where
    I: IntoIterator<Item = S>,
    S: AsRef<str>,
{
    args.into_iter().any(|arg| arg.as_ref() == "--quit")
}

#[cfg(test)]
mod tests {
    use super::wants_quit;

    #[test]
    fn quit_flag_is_absent_by_default() {
        assert!(!wants_quit(["kyberdash-tray"]));
        assert!(!wants_quit(Vec::<&str>::new()));
    }

    #[test]
    fn quit_flag_matches_exactly() {
        assert!(wants_quit(["kyberdash-tray", "--quit"]));
        assert!(wants_quit(["--quit"]));
        assert!(wants_quit(["kyberdash-tray", "--verbose", "--quit"]));
        assert!(!wants_quit(["kyberdash-tray", "--quit-now"]));
        assert!(!wants_quit(["kyberdash-tray", "-quit"]));
        assert!(!wants_quit(["kyberdash-tray", "--Quit"]));
    }
}
