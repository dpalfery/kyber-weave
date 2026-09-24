# Lens: authz-tenancy

## Applicability

Applies when the diff adds or changes an endpoint, handler, command, query, or any code path
that reads or writes data belonging to a user, an account, an organization, or a tenant.

Skip when no such path is touched.

## What this lens owns

Whether the right caller can reach the right data, and only that data. Authentication
establishes who is calling; you own everything after that.

## What to look for

**Missing authorization, not just missing authentication.** A path behind a login is not
thereby authorized. For every resource access, find the check that this particular caller may
touch this particular resource. A check performed once at the entry point does not cover a
later access with a different identifier.

**Identifiers from the request.** The classic and still the most common: a record fetched by
an id taken from the caller, with no verification that the record belongs to them. Change the
id, get someone else's data. Trace every identifier in the request to the check that
constrains it.

**Tenant scoping that depends on remembering.** A query filtered by tenant in nine places and
not the tenth is a data leak with a long fuse. Look for scoping applied by convention rather
than enforced by construction — a repository that can express an unscoped query will
eventually be asked to.

**Ambient identity.** Context that carries the caller's identity but is set somewhere far
from where it is read, particularly across an async boundary, a background job, a queue
consumer, or a cache. A cached value keyed without the tenant is the same defect as an
unscoped query.

**Privilege boundaries within the change.** New roles, new permissions, a widened existing
permission, a check moved from server to client, an administrative path that shares a handler
with a user path.

**Elevation through a side door.** Batch endpoints, export paths, search, webhooks, and
error messages that reveal the existence of resources the caller cannot read.

## What this lens must not report

- Client-side checks as vulnerabilities in themselves. The server is responsible; a missing
  client check is a usability matter unless the server check is also absent — in which case
  the finding is the server one.
- Missing authorization on a path that is genuinely public by design.
- Injection, crypto, or secret handling — the security lens owns those.
- Rate limiting or abuse prevention.
- Authorization on a path whose scoping is enforced by a framework mechanism you can see
  applied. Verify before reporting; the check may not look like an `if`.
