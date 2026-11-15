# copilot-chat skills captures

Real `invoke_agent` span from source `github-copilot` in the local store. The listing key is `github.copilot.context.skills` (a JSON-array string of names), NOT `gen_ai.skills`. The system prompt part also carries an `<available_skills>` XML block (name, description, location); a trimmed sample is `capture-listing-system-prompt.xml`. Invocation: no `skill` tool record exists in the local store (only view, task, bash, edit tool spans were present). Trimmed to repository skill names.
