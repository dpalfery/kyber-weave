<!-- One copy of the task difficulty rubric. The architect, its plan-authoring reference, the product-owner tasks reference and the decision model's agreement check all use this text; a content test keeps the copies identical. -->

Every file-changing task carries a `Difficulty:` line. The values are still spelled `easy`, `medium` and `hard`; read them as **standard**, **intermediate** and **advanced**. The tier says how much can go wrong that a worker would not see from an ordinary test, not how much work the task is.

**Standard (`easy`) is the default: about eight tasks in ten.** It is idiomatic production code in one technology, however large: endpoints, multi-table queries, UI state, validation, business rules, and any pattern the codebase already has. If it is wrong, a normal test fails or a user sees it at once. Build the simplest clean solution; add no queue, lock, cache or defensive layer the task does not need.

**Intermediate (`medium`): the happy path works, but the code fails at scale, across tenants, or when the other side misbehaves, unless a known defensive pattern is applied.** Ask: what happens when there is a lot of it, many users or tenants share it, or a dependency is slow or flaky? Contrast pairs:
- A paged admin list over a table that rarely changes is standard. An infinite-scroll feed while new rows keep arriving is intermediate: page numbers skip and repeat items, so it needs a cursor.
- Checking a role on an endpoint, like the endpoints beside it, is standard. Making every query return only the caller's own team's or organisation's rows is intermediate.
- Calling an internal service the app already calls is standard. Calling a third-party API that drops requests is intermediate: retries need exponential backoff with jitter, or a recovering server is flattened.
- Sending one email inside the request is standard. A bulk export or mass email is intermediate: it moves to a background job, and a job that keeps failing goes to a dead-letter queue instead of being lost or retried forever.
- Logging an error is standard. Following one user's request through the frontend, API and database logs is intermediate: one correlation ID travels through all of them.

**Advanced (`hard`): a mistake silently corrupts data, leaks access, or charges money, and shows up only under timing, retries, a crash part-way through, or clock and timezone edges, so no ordinary test run reveals it.** Ask: can this go wrong in a way no test will show, and would it cost money, data or trust? Contrast pairs:
- Saving an order is standard. Handling a payment webhook that can arrive twice or out of order is advanced: it needs an idempotency key.
- Updating a counter in one request is standard. Two buyers reserving the last item at the same moment, on different servers, is advanced: it needs locking that survives clock drift and expired locks.
- Writing a row is standard. Writing a row and publishing the event billing depends on is advanced: a crash between the two leaves them out of step, so it needs a transactional outbox.
- A form that saves in one step is standard. A booking across several services that must undo earlier steps when a later one fails is advanced: a saga with compensating steps.
- Reading cached settings is standard. Keeping several cache layers consistent when data changes is advanced: cache busting.
- Adding the authorization attribute the neighbouring endpoints use is standard. Building sign-in with SSO, OAuth or OIDC, or the token lifecycle, is advanced.
- Adding a nullable column is standard. Renaming or reshaping a column on a live table without downtime is advanced: an expand-and-contract migration.
- Showing a date is standard. A recurring schedule across timezones and daylight-saving changes is advanced: store UTC plus the IANA zone rule.

These pairs teach the line; they are not the whole list. Rate a task by which side of the line it falls on, by the same reasoning, even when nothing above names it. When the codebase already has a proven implementation of an intermediate or advanced pattern and the task reuses it as it is, the task is standard.

For an advanced task, the plan does the design before any worker starts: state the failure mode in one line and the mitigation in three to five bullets, in the task itself, so the worker builds what is already decided.

Rate the decisions the worker must make, not the size: the number of files or layers does not by itself raise the rating. Split the work so each task follows one existing pattern the plan names, in one technology, and say which file to copy. When unsure between two tiers, pick the lower, because a failed attempt moves the task one tier up.
