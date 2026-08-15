---
name: browser-testing
description: Test web applications and solutions using Playwright MCP tools. Use when validating functionality, running end-to-end tests, verifying user flows, or testing solutions that have been built. Includes automated testing, visual verification, and test assertion capabilities.
license: MIT
metadata:
  author: David R Palfery
  version: 1.0.0
---

# Playwright Testing Skill

This skill helps you test web applications and solutions using the Playwright MCP server tools. Use this when you need to validate that a solution works correctly, test user interactions, or create automated test scenarios.

## When to Use This Skill

- Testing a web application or solution after building it
- Validating that user flows work correctly
- Verifying UI elements are visible and interactive
- Creating automated test scenarios
- Debugging issues with web interactions
- Running end-to-end tests on web pages
- Checking form submissions and navigation
- Validating responsive behavior

## Core Testing Workflow

### 1. Initialize Testing Session

Start by navigating to the page you want to test:

```
Use browser_navigate to go to the URL of the solution
Take a browser_snapshot to see the current state
```

### 2. Test User Interactions

Test critical user flows step by step:

```
- Use browser_click to interact with buttons and links
- Use browser_type to fill in form fields
- Use browser_select_option for dropdowns
- Use browser_fill_form for multiple fields at once
- Verify expected behavior after each action
```

### 3. Verify Results

After each interaction, verify the results:

```
- Use browser_snapshot to see the new page state
- Check for expected text, elements, or changes
- Use browser_console_messages to check for errors
- Use browser_network_requests to verify API calls
```

## Testing Best Practices

### Progressive Testing

1. **Start with navigation**: Verify the page loads correctly
2. **Test individual elements**: Check each interactive element works
3. **Test complete flows**: Walk through full user scenarios
4. **Verify edge cases**: Test error states and boundary conditions
5. **Check across browsers**: Test in different environments if needed

### Verification Strategy

Always verify after actions using these approaches:

- **Visual verification**: Take snapshots and check for expected elements
- **Console verification**: Check for JavaScript errors or warnings
- **Network verification**: Ensure API calls complete successfully
- **State verification**: Confirm page state matches expectations

### Common Test Patterns

**Form Testing Pattern**:
```
1. Navigate to form page
2. Take snapshot to identify form fields
3. Fill all required fields using browser_fill_form
4. Click submit button
5. Wait for response using browser_wait_for
6. Verify success message or error handling
7. Check console for errors
```

**Navigation Testing Pattern**:
```
1. Start at homepage
2. Click navigation links one by one
3. Verify each page loads correctly
4. Check back navigation with browser_navigate_back
5. Ensure no console errors on any page
```

**Interactive Feature Testing Pattern**:
```
1. Navigate to feature page
2. Take initial snapshot
3. Perform interaction (click, hover, etc.)
4. Wait for expected change
5. Take new snapshot to verify changes
6. Test edge cases (rapid clicks, etc.)
```

## Available Playwright Tools

### Navigation & State
- `browser_navigate` - Go to a URL
- `browser_navigate_back` - Go back in history
- `browser_snapshot` - Capture accessibility tree (preferred over screenshots)
- `browser_take_screenshot` - Capture visual screenshot when needed
- `browser_resize` - Test responsive layouts

### Interactions
- `browser_click` - Click elements (supports double-click, modifiers)
- `browser_type` - Type text into fields
- `browser_fill_form` - Fill multiple form fields at once
- `browser_select_option` - Select dropdown options
- `browser_hover` - Hover over elements
- `browser_drag` - Drag and drop between elements
- `browser_press_key` - Press keyboard keys
- `browser_file_upload` - Upload files to inputs

### Verification & Debugging
- `browser_wait_for` - Wait for text to appear/disappear or time to pass
- `browser_console_messages` - Get console logs (filter errors with onlyErrors)
- `browser_network_requests` - List all network requests
- `browser_evaluate` - Run JavaScript for custom checks
- `browser_run_code` - Execute Playwright code directly

### Advanced Testing
- `browser_handle_dialog` - Accept/dismiss alerts, confirms, prompts
- `browser_tabs` - Manage multiple browser tabs
- `browser_generate_locator` - Create test locators for elements

## Testing Checklist

When testing a solution, verify:

- [ ] Page loads without errors
- [ ] All interactive elements are clickable
- [ ] Forms accept input and validate correctly
- [ ] Submit actions complete successfully
- [ ] Navigation between pages works
- [ ] No console errors or warnings
- [ ] Network requests complete as expected
- [ ] Visual elements render correctly
- [ ] Responsive behavior works (if applicable)
- [ ] Error states are handled gracefully

## Example Test Scenarios

### Example 1: Testing a Contact Form

```
1. Navigate to contact form page
2. Snapshot the page to identify form fields
3. Fill form with test data:
   - Name: "Test User"
   - Email: "test@example.com"
   - Message: "This is a test message"
4. Click submit button
5. Wait for success message
6. Check console for errors
7. Verify network request was made
```

### Example 2: Testing Multi-Step Workflow

```
1. Navigate to starting page
2. Complete step 1 (e.g., user registration)
3. Verify step 1 completion
4. Navigate to step 2 (e.g., profile setup)
5. Complete step 2 fields
6. Verify step 2 completion
7. Complete final step
8. Verify entire flow succeeded
9. Check for errors throughout
```

### Example 3: Testing Search Functionality

```
1. Navigate to page with search
2. Locate search input field
3. Type search query
4. Submit search (click or press Enter)
5. Wait for results to appear
6. Verify results contain expected content
7. Test edge cases (empty search, special characters)
8. Check network requests for search API calls
```

## Error Handling

When tests fail:

1. **Check console messages**: Use `browser_console_messages(onlyErrors=true)`
2. **Review network requests**: Use `browser_network_requests` to see failed API calls
3. **Take screenshots**: Use `browser_take_screenshot` to capture visual state
4. **Check element states**: Use `browser_evaluate` to inspect element properties
5. **Verify timing**: Add `browser_wait_for` if elements load asynchronously

## Tips for Effective Testing

1. **Always take snapshots first**: Use `browser_snapshot` before interactions to see available elements
2. **Use exact refs**: When clicking or typing, use the exact `ref` values from snapshots
3. **Wait appropriately**: Use `browser_wait_for` for dynamic content rather than arbitrary delays
4. **Test incrementally**: Verify each step before proceeding to the next
5. **Check for errors**: Always review console messages after important actions
6. **Document findings**: Keep notes on what works and what doesn't
7. **Test edge cases**: Don't just test the happy path
8. **Use browser_run_code**: For complex validations, write custom Playwright code

## References

- **Playwright MCP Documentation**: https://github.com/microsoft/playwright-mcp
- **Codex Skills**: https://developers.openai.com/codex/skills
- **Agent Skills Overview**: https://agentskills.io/specification
