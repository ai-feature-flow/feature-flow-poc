# R4 audit: every finding, with the rubric verdict

## I-A (I-A.diff, spec A): 0/20

- pass 1: no findings
- pass 2: no findings
- pass 3 **no** `null-input-behaviour` WidgetStore.Normalise: Normalise dereferences name without a null check, so a null name that Add previously stored now throws NullReferenceException, and Program.cs passes request.Name unchanged; confirm the request name cannot be null or guard it.
- pass 4: no findings
- pass 5: no findings
- pass 6 **no** `null-safety` WidgetStore.Normalise: Normalise dereferences name without a null check, so a null name now throws NullReferenceException inside the lock where previously it was stored; consider validating or guarding against null.
- pass 7: no findings
- pass 8 **no** `null-input-regression` WidgetStore.Normalise: Normalise dereferences name without a null check, so a null name that Add previously stored now throws NullReferenceException; the ticket does not say how null should be handled.
- pass 9: no findings
- pass 10: no findings
- pass 11 **no** `null-input-regression` WidgetStore.Normalise: Normalise has no null guard, so a null name (which Program.cs can pass from request.Name) now throws NullReferenceException where Add previously stored it; confirm nullability or add a guard.
- pass 12: no findings
- pass 13: no findings
- pass 14: no findings
- pass 15: no findings
- pass 16: no findings
- pass 17: no findings
- pass 18: no findings
- pass 19 **no** `null-input-behaviour-change` WidgetStore.Normalise: Normalise dereferences name without a null check, so a null name that was previously stored now throws NullReferenceException from Add; confirm the request path validates Name or add a guard.
- pass 20: no findings

## I-B (I-B.diff, spec B): 0/20

- pass 1: no findings
- pass 2: no findings
- pass 3: no findings
- pass 4: no findings
- pass 5: no findings
- pass 6: no findings
- pass 7: no findings
- pass 8: no findings
- pass 9: no findings
- pass 10: no findings
- pass 11: no findings
- pass 12: no findings
- pass 13: no findings
- pass 14: no findings
- pass 15: no findings
- pass 16: no findings
- pass 17: no findings
- pass 18: no findings
- pass 19: no findings
- pass 20: no findings

## I-epic (I-epic.diff, spec epic): 20/20

- pass 1 **DETECTS** `ticket-requirement-lookup-case-insensitive` FindByName: The plan mandates a case-sensitive lookup and asserts FindByName("ALPHA") returns null, which contradicts the epic requirement that a client can look a widget up by its creation name in any case or spacing.
- pass 1 **DETECTS** `ticket-requirement-lookup-case-insensitive` app.MapGet("/widgets/by-name/{name}"): The by-name route passes the raw path segment to FindByName without trimming or lower-casing, so lookups such as "ALPHA" or " Alpha " will return 404 despite the widget existing under the normalised name.
- pass 1 **no** `unverified-compile-impact` Widget: Adding a third required positional parameter to Widget will break any existing two-argument `new Widget(...)` call sites, and the plan states these were not checked (IntegrationTests/WidgetRepositoryTests.cs was not read).
- pass 1 **DETECTS** `truncated-diff-unverifiable` WidgetStore.Add / FindByName: The WidgetStore.cs hunk is truncated, so the Normalise application in Add, the CreatedAt assignment, and the FindByName implementation cannot be verified from the diff.
- pass 2 **DETECTS** `ticket-requirement-case-insensitive-lookup` FindByName: The epic requires a client to look a widget up by its creation name in any case or spacing, but the planned FindByName is ordinal and case-sensitive and its test asserts FindByName("ALPHA") returns null, so this contradicts the ticket.
- pass 2 **DETECTS** `ticket-requirement-case-insensitive-lookup` /widgets/by-name/{name}: The route passes the raw path segment straight to FindByName without normalising it, so GET /widgets/by-name/ALPHA or a padded name returns 404 even though the widget was created as "Alpha".
- pass 2 **no** `unverified-call-sites` Widget: The Widget record gains a required third parameter, and the plan itself admits other two-argument `new Widget(...)` call sites (e.g. IntegrationTests/WidgetRepositoryTests.cs) were not checked, so the build may break.
- pass 3 **DETECTS** `ticket-requirement: case- and whitespace-insensitive lookup` WidgetStore.FindByName: The epic requires a client to look a widget up by its creation name in any case or spacing, but the planned FindByName is ordinal and case-sensitive and the planned test asserts FindByName("ALPHA") returns null, so the route will 404 for names the epic says must resolve.
- pass 3 **no** `ticket-requirement: case- and whitespace-insensitive lookup` /widgets/by-name/{name}: The route passes the raw path segment straight to FindByName with no trimming or lower-casing, so lookups with different case or surrounding whitespace will not find the widget.
- pass 3 **DETECTS** `unverifiable-change` WidgetStore.Add / FindByName: The WidgetStore.cs hunk is truncated, so the Normalise call in Add, the CreatedAt assignment, and the FindByName method that Program.cs calls cannot be confirmed from the diff.
- pass 4 **DETECTS** `ticket-requirement: case/whitespace-insensitive lookup` GET /widgets/by-name/{name}: The new route delegates to FindByName, which EPIC3-B specifies as an ordinal, case-sensitive match against normalised names, so GET /widgets/by-name/ALPHA returns 404 for a widget created as "Alpha" and the ticket's any-case, any-spacing lookup requirement is not met.
- pass 4 **DETECTS** `unverifiable-diff` WidgetStore.Add / FindByName: The WidgetStore.cs hunk is truncated, so the Normalise call in Add, the CreatedAt argument, and the FindByName body cannot be verified, and other two-argument `new Widget(...)` call sites (noted as unverified in the plan) would fail to compile after the record change.
- pass 5 **DETECTS** `ticket-requirement: case/whitespace-insensitive lookup` WidgetStore.FindByName: The plan specifies a case-sensitive lookup and expects FindByName("ALPHA") to return null, which contradicts the epic requirement that a client can look a widget up by its creation name in any case or spacing.
- pass 5 **DETECTS** `ticket-requirement: case/whitespace-insensitive lookup` MapGet /widgets/by-name/{name}: The by-name route passes the raw path segment to FindByName without trimming or lower-casing it, so a client requesting "Alpha" or " alpha " gets a 404 even though the store holds "alpha".
- pass 5 **DETECTS** `unverified-change` WidgetStore.Add: The WidgetStore.cs diff is truncated, so the Add body (Normalise call, DateTimeOffset.UtcNow argument) and the FindByName implementation cannot be verified from the diff.
- pass 5 **no** `unverified-change` Widget record: The plan records that other two-argument `new Widget(...)` call sites, including integration tests, were not checked and may break compilation after adding CreatedAt.
- pass 6 **DETECTS** `ticket-requirement: case- and whitespace-insensitive lookup` GET /widgets/by-name/{name}: The ticket requires a client to look a widget up by its creation name in any case or spacing, but the planned FindByName is an ordinal, case-sensitive match on the raw input with no normalisation, so FindByName("Alpha") or FindByName("ALPHA") for a widget created as "Alpha" returns 404.
- pass 6 **DETECTS** `ticket-requirement: case- and whitespace-insensitive lookup` FindByName: The plan specifies case-sensitive lookup and a test asserting FindByName("ALPHA") returns null, which encodes the behaviour the ticket forbids and should instead normalise the query before comparing.
- pass 6 **no** `compile-risk: Widget record arity change` Widget: Changing Widget to three positional parameters breaks any remaining two-argument construction sites, and the plan admits the integration test file WidgetRepositoryTests.cs was never checked.
- pass 7 **DETECTS** `ticket-requirement: case-insensitive lookup by creation name` WidgetStore.FindByName: The planned FindByName is case-sensitive and the plan expects FindByName("ALPHA") to return null, which contradicts the ticket's requirement that a client can look a widget up by its creation name in any case or spacing; the new /widgets/by-name route in Program.cs inherits this bug.
- pass 8 **DETECTS** `ticket-requirement: case- and whitespace-insensitive lookup` MapGet /widgets/by-name/{name}: The new by-name route passes the raw path segment to FindByName, which the EPIC3-B plan specifies as an ordinal, case-sensitive match with no trim or lower-casing, so a client cannot find a widget by the name it created it with in other case or spacing, contrary to the epic.
- pass 9 **DETECTS** `ticket-case-insensitive-lookup` FindByName: The ticket requires a client to find a widget by its creation name in any case or spacing, but this plan makes lookup case-sensitive and specifies a test asserting FindByName("ALPHA") returns null, which codifies the violation.
- pass 9 **DETECTS** `ticket-case-insensitive-lookup` GET /widgets/by-name/{name}: The route passes the raw client-supplied name to FindByName without trimming or lower-casing, so a lookup such as "ALPHA" or " alpha " returns 404 even though the widget was created as "  Alpha ".
- pass 9 **no** `unverified-compile-risk` Widget: The plan admits call sites in tests/FeatureFlow.Example.Api.IntegrationTests/WidgetRepositoryTests.cs were not checked after the Widget record gained a required third parameter, so the build may break.
- pass 10 **DETECTS** `ticket-requirement: case/whitespace-insensitive lookup` app.MapGet /widgets/by-name/{name}: The new lookup route delegates to FindByName, which the EPIC3-B plan defines as an ordinal, case-sensitive match on the raw input, so a client cannot find a widget by the name it created it with in other casing or spacing, as the ticket requires.
- pass 10 **DETECTS** `ticket-requirement: case/whitespace-insensitive lookup` FindByName: The plan requires FindByName("ALPHA") to return null and performs no normalisation of the query, which contradicts the ticket's requirement that lookup works in any case or spacing.
- pass 11 **DETECTS** `ticket-requirement: case-insensitive name lookup` WidgetStore.FindByName: The planned FindByName is ordinal and case-sensitive, so a client cannot find a widget by the name it was created with in any case, which the epic requires; the stored names are lower-cased, so FindByName("Alpha") would also miss.
- pass 11 **DETECTS** `ticket-requirement: whitespace-insensitive name lookup` /widgets/by-name/{name}: The by-name route passes the raw route value to FindByName without trimming or normalising, so names with surrounding whitespace cannot be looked up as the epic requires.
- pass 11 **no** `unverified-compile-impact: Widget record arity` Widget: Adding a required third positional parameter breaks any remaining two-argument `new Widget(...)` call sites, and the plan itself says the integration test file that may contain one was not read.
- pass 12 **DETECTS** `ticket-requirement-case-insensitive-lookup` WidgetStore.FindByName: The ticket requires a client to find a widget by the name it was created with in any case or spacing, but this plan makes FindByName case-sensitive and the route passes the raw name through, so GET /widgets/by-name/ALPHA or /widgets/by-name/%20Alpha%20 cannot find a widget stored as "alpha". The planned test that asserts FindByName("ALPHA") returns null encodes the wrong behaviour.
- pass 12 **no** `compile-risk-widget-constructor-callers` Widget: The Widget record gains a required third parameter, and the plan admits other two-argument `new Widget(` call sites (including IntegrationTests/WidgetRepositoryTests.cs) were not checked, so the build may break.
- pass 13 **DETECTS** `ticket-requirement: case- and whitespace-insensitive lookup` WidgetStore.FindByName: The planned FindByName is case-sensitive and its test asserts FindByName("ALPHA") returns null, which contradicts the ticket requirement that a client can look a widget up by its creation name in any case or spacing.
- pass 13 **DETECTS** `ticket-requirement: case- and whitespace-insensitive lookup` MapGet /widgets/by-name/{name}: The by-name route passes the raw path value to FindByName without trimming or lower-casing, so lookups using different case or padding will return 404 instead of the widget.
- pass 13 **no** `unverified-change-risk` Widget record: Changing Widget to a three-argument positional record will break any unseen two-argument `new Widget(...)` call sites, and the diff does not show that these were checked or updated.
- pass 14 **DETECTS** `ticket-requirement: case- and whitespace-insensitive lookup` WidgetStore.FindByName: The planned FindByName is case-sensitive and does not normalise its input, so a client looking up a widget by the name it created it with in a different case or spacing gets 404, which contradicts the ticket; its test also asserts FindByName("ALPHA") returns null.
- pass 14 **no** `unverified-compile-risk: Widget record arity change` Widget: Changing Widget to three positional parameters may break existing two-argument call sites such as IntegrationTests/WidgetRepositoryTests.cs, which the plan says were not checked and the diff does not show being updated.
- pass 15 **DETECTS** `ticket-requirement-conflict` WidgetStore.FindByName: The epic requires a client to look a widget up by the name it created it with in any case or spacing, but this plan specifies a case-sensitive ordinal lookup and a test asserting FindByName("ALPHA") returns null, so the GET /widgets/by-name/{name} route would return 404 for Add("Alpha") followed by a lookup of "Alpha".
- pass 15 **DETECTS** `unverified-implementation` WidgetStore: The WidgetStore.cs hunk is truncated, so the Normalise helper, the Add change, and the FindByName method that Program.cs now calls cannot be checked for the required normalisation or for compilation.
- pass 16 **DETECTS** `ticket-requirement: case-insensitive lookup by creation name` WidgetStore.FindByName: The plan makes FindByName case-sensitive and expects FindByName("ALPHA") to return null, which contradicts the ticket requirement that a client can look a widget up by its creation name in any case or spacing, and Program.cs's new by-name route depends on this method.
- pass 17 **DETECTS** `ticket-requirement` WidgetStore.FindByName: The plan specifies a case-sensitive lookup and a test that FindByName("ALPHA") returns null, which contradicts the ticket requirement that a widget can be found by its creation name in any case or spacing.
- pass 17 **DETECTS** `ticket-requirement` app.MapGet("/widgets/by-name/{name}"): The by-name route passes the raw route value to FindByName without trimming or lower-casing, so lookups with different case or surrounding whitespace will return 404 even though the ticket requires them to succeed.
- pass 17 **no** `unverified-change` Widget: The plan notes that other two-argument `new Widget(...)` call sites, including the integration tests, were not checked, so the record change may break compilation and the diff does not show those files were updated.
- pass 18 **DETECTS** `case-insensitive-lookup` MapGet /widgets/by-name/{name}: The by-name route passes the raw client string to FindByName without trimming or lower-casing, and the EPIC3-B plan makes FindByName ordinal and case-sensitive, so a client cannot find a widget by the name it created it with in any case or spacing, which the epic requires.
- pass 18 **DETECTS** `case-insensitive-lookup` FindByName: The planned test asserts that a differently-cased lookup returns null, which directly contradicts the epic requirement that lookup works in any case or spacing.
- pass 18 **DETECTS** `diff-completeness` WidgetStore.Add / FindByName: The WidgetStore.cs hunk is truncated, so the Add normalisation and the FindByName implementation cannot be verified from the diff.
- pass 19 **DETECTS** `ticket-requirement: case/whitespace-insensitive lookup` WidgetStore.FindByName: The planned FindByName is ordinal and case-sensitive, and the new Program.cs route passes the raw route value straight to it without normalising, so a client that created "  Alpha " cannot look it up by that name. This contradicts the ticket requirement that lookup works in any case or spacing.
- pass 19 **no** `unverified-compile-impact: Widget record signature change` Widget: The record gained a required third parameter, and the plan states that other two-argument `new Widget(...)` call sites, including tests/FeatureFlow.Example.Api.IntegrationTests/WidgetRepositoryTests.cs, were not checked and may fail to compile.
- pass 20 **DETECTS** `ticket-requirement-mismatch` WidgetStore.FindByName: The ticket requires a client to find a widget by its creation name in any case or spacing, but this plan makes FindByName case-sensitive and expects a lookup for "ALPHA" to return null, and Program.cs passes the raw route value without normalising it.

## Neg (Neg.diff, spec epic): 15/20

- pass 1 **no** `compile-break-unverified-callsites` Widget: Adding a required third positional parameter breaks every existing two-argument `new Widget(...)` call, and the plan admits the integration test call sites (WidgetRepositoryTests.cs) were never checked or updated in this diff.
- pass 1 **DETECTS** `diff-incomplete-review` WidgetStore.Add / Normalise / FindByName: The WidgetStore.cs hunk is truncated, so the Normalise helper, the Add change, the lock-scoped CreatedAt assignment and the FindByName method that Program.cs calls cannot be verified from the diff, and the planned test files are not shown.
- pass 2 **DETECTS** `unverifiable-change` WidgetStore.FindByName: Program.cs calls WidgetStore.FindByName, but the WidgetStore.cs hunk is truncated before any FindByName addition, so the lookup's normalisation and its placement after Count() cannot be confirmed.
- pass 2 **no** `unverifiable-change` Widget: The plan says the other two-argument `new Widget(...)` call sites were never checked and may no longer compile after the CreatedAt parameter was added, and the diff does not show them being updated.
- pass 2 **DETECTS** `missing-test` WidgetNameNormalisationTests: The plan lists new test files (WidgetNameNormalisationTests.cs, WidgetFindByNameTests.cs, WidgetCreatedAtTests.cs), but none of them appear in the diff, which may be truncated.
- pass 3 **no** `incomplete-diff-evidence` WidgetStore.Add / FindByName: The WidgetStore.cs hunk is cut off before the Add body, so the diff does not show that Add applies Normalise, sets CreatedAt, or that FindByName exists, although Program.cs already calls store.FindByName.
- pass 3 **no** `compile-break-unverified-call-sites` Widget: Widget gains a required third parameter, and the plan says other two-argument `new Widget(` call sites, including IntegrationTests/WidgetRepositoryTests.cs, were not checked and may no longer compile.
- pass 4 **DETECTS** `diff-truncated-unverifiable` WidgetStore.Add / FindByName: The WidgetStore.cs hunk is cut off, so the diff does not show Normalise being applied in Add, the CreatedAt argument, or the FindByName method that Program.cs now calls; these cannot be verified and FindByName's absence would break compilation.
- pass 4 **DETECTS** `planned-tests-missing` WidgetNameNormalisationTests: The plans call for WidgetNameNormalisationTests.cs, WidgetFindByNameTests.cs and WidgetCreatedAtTests.cs, but none of these new test files appear in the diff, so the normalisation, lookup and CreatedAt behaviour is untested.
- pass 4 **no** `widget-ctor-call-sites` Widget: The Widget record gained a required third parameter, and the plan itself notes that other two-argument `new Widget(...)` call sites, including in the integration tests, were not checked and may no longer compile.
- pass 5 **DETECTS** `unverified-change` WidgetStore.Add / Normalise / FindByName: The WidgetStore.cs hunk is truncated, so the Add, Normalise, and FindByName changes that Program.cs depends on cannot be reviewed from this diff.
- pass 5 **no** `compile-break-risk` Widget: The Widget record gained a required third parameter, and the plan itself notes that other two-argument `new Widget(` call sites, such as in WidgetRepositoryTests.cs, were not checked and may no longer compile.
- pass 6 **DETECTS** `unverified-change-coverage` WidgetStore.Add / FindByName: The WidgetStore.cs hunk is truncated, so the Normalise call in Add, the CreatedAt argument, and the FindByName body cannot be verified from the diff.
- pass 6 **no** `unverified-call-sites` Widget: The Widget record gained a required third parameter, and the plan notes that other two-argument `new Widget(` call sites, including IntegrationTests/WidgetRepositoryTests.cs, were not checked and may not compile.
- pass 7 **DETECTS** `diff-coverage` WidgetStore.Add / FindByName: The WidgetStore.cs hunk is truncated, so the Normalise call in Add, the CreatedAt assignment, and the FindByName body that Program.cs now calls cannot be verified from the diff.
- pass 7 **no** `compile-risk` Widget: The Widget record now requires a third CreatedAt argument, and the plan admits other two-argument `new Widget(` call sites (including the integration tests) were never checked, so the build may break.
- pass 8 **no** `compile-call-site-coverage` Widget: The Widget record gains a required third parameter, but the visible diff does not show any other two-argument `new Widget(...)` call sites being updated, and the plan itself notes the grep was denied and IntegrationTests/WidgetRepositoryTests.cs was not read, so the build may break.
- pass 9 **DETECTS** `incomplete-diff-verification` WidgetStore.Add / FindByName: The WidgetStore.cs hunk is truncated before the Add body, so the Normalise call, the CreatedAt argument, and the FindByName method that Program.cs now calls cannot be confirmed from the diff.
- pass 9 **no** `compile-risk-unverified-call-sites` Widget: Adding a required third parameter to Widget will break any existing two-argument `new Widget(...)` call sites, and the plan says the grep for them was denied and IntegrationTests/WidgetRepositoryTests.cs was not read, so the change may not compile.
- pass 9 **DETECTS** `missing-test-files-in-diff` WidgetNameNormalisationTests, WidgetFindByNameTests, WidgetCreatedAtTests: The plans require three new unit test files, but none of them appear in the visible diff, so the planned test coverage cannot be confirmed.
- pass 10 **DETECTS** `incomplete-diff-review` WidgetStore: The diff is truncated mid-hunk, so the Add body (Normalise call and CreatedAt assignment) and the FindByName method that Program.cs now calls cannot be confirmed from the shown diff.
- pass 10 **no** `compile-risk-unverified-call-sites` Widget: Adding a required third parameter to Widget breaks any two-argument `new Widget(...)` call site, and the plan notes the grep was denied and WidgetRepositoryTests.cs was not read, so the integration tests may fail to compile.
- pass 10 **DETECTS** `missing-test-coverage-in-diff` WidgetNameNormalisationTests, WidgetFindByNameTests, WidgetCreatedAtTests: The three new test files named in the plans do not appear in the visible diff, so the normalisation, FindByName, and CreatedAt behaviour is not shown to be tested.
- pass 11 **no** `unverified-call-sites` Widget: The Widget record gains a required third parameter, and the plan notes that other two-argument `new Widget(` call sites (e.g. IntegrationTests/WidgetRepositoryTests.cs) were not checked, so they may fail to compile.
- pass 11 **DETECTS** `incomplete-diff` WidgetStore: The WidgetStore.cs hunk is truncated, so the FindByName implementation and the Normalise/CreatedAt wiring in Add cannot be verified from the diff, and the new route in Program.cs depends on FindByName.
- pass 12 **no** `compile-break-unverified-call-sites` Widget: The Widget record gains a required third parameter, and the plan itself says other two-argument `new Widget(` call sites (including IntegrationTests/WidgetRepositoryTests.cs) were never checked, yet the diff contains no changes to them, so the solution may not compile.
- pass 12 **DETECTS** `diff-truncated-unverifiable` WidgetStore: The WidgetStore.cs hunk is cut off, so Normalise, the Add change, FindByName (which Program.cs now calls), and the CreatedAt assignment cannot be confirmed from the diff.
- pass 13 **no** `record-arity-change-unverified-call-sites` Widget: Adding a required third positional parameter breaks any remaining two-argument `new Widget(...)` call; the plan says WidgetRepositoryTests.cs was never read, and the diff shows no update to it, so the build may fail.
- pass 13 **DETECTS** `diff-truncated-unverifiable` WidgetStore: The diff is cut off inside WidgetStore.cs, so Normalise, the CreatedAt argument in Add, FindByName, and the three planned new test files cannot be checked against the ticket from what is shown.
- pass 14 **no** `unverified-compile-compat` Widget: The Widget record gains a required third parameter, and the plan notes that other two-argument `new Widget(...)` call sites (e.g. IntegrationTests/WidgetRepositoryTests.cs) were not checked and may no longer compile.
- pass 14 **DETECTS** `incomplete-diff` WidgetStore.Add / FindByName: The WidgetStore.cs hunk is truncated, so the Add body (Normalise, CreatedAt inside the lock) and the FindByName method that Program.cs calls cannot be verified from this diff.
- pass 15: no findings
- pass 16 **no** `compile-risk-unverified-call-sites` Widget: The Widget record gained a required third parameter, and EPIC3-C says other two-argument `new Widget(...)` call sites (including IntegrationTests/WidgetRepositoryTests.cs) were never checked or updated, so the solution may not compile.
- pass 16 **DETECTS** `missing-tests-from-plan` WidgetNameNormalisationTests: The three new unit test files named in the EPIC3 plans do not appear in the visible diff, so the normalisation, lookup, and CreatedAt behaviour are not covered by tests in this change.
- pass 17 **DETECTS** `diff-completeness` WidgetStore.FindByName: Program.cs calls WidgetStore.FindByName, but the WidgetStore.cs hunk is truncated, so the definition of FindByName and the Normalise/CreatedAt wiring in Add cannot be confirmed from the diff.
- pass 18 **no** `unverifiable-change` WidgetStore.Add / FindByName: The WidgetStore.cs hunk is cut off, so the diff does not show the Normalise call in Add, the new CreatedAt argument, or the FindByName method that Program.cs now calls; these must be checked in the full file before merge.
- pass 18 **no** `compile-risk` Widget: Adding a required third parameter breaks any remaining two-argument `new Widget(...)` call sites, and the plan says the grep for them was denied and IntegrationTests/WidgetRepositoryTests.cs was not read, so the build is unverified.
- pass 19 **DETECTS** `incomplete-diff-verification` WidgetStore.FindByName: Program.cs calls store.FindByName, but the WidgetStore.cs hunk in the diff is truncated before any FindByName or Normalise body is visible, so the lookup normalisation and the Add change cannot be confirmed from this diff.
- pass 19 **no** `incomplete-diff-verification` Widget: The Widget record gains a required third parameter, and the plan notes that other two-argument new Widget(...) call sites (e.g. WidgetRepositoryTests.cs) were not checked and may fail to compile.
- pass 20 **no** `unverified-call-sites` Widget: Changing the Widget record to three positional parameters will break any unseen two-argument `new Widget(...)` call sites, and the integration tests were never checked, so the build may fail.

## Pos (Pos.diff, spec epic): 5/5

- pass 1 **DETECTS** `ticket-requirement: lookup by name in any case or spacing` app.MapGet("/widgets/by-name/{name}"): The by-name route passes the raw URL segment to FindByName, which per docs/plans/EPIC3-B.md is an ordinal, case-sensitive comparison with no normalisation, so GET /widgets/by-name/ALPHA or a padded name returns 404 even though the widget was created as "Alpha", violating the ticket's requirement that lookup works in any case or spacing.
- pass 2 **DETECTS** `ticket-requirement-case-insensitive-lookup` FindByName: The plan makes FindByName case-sensitive and unnormalised, so a client cannot find a widget by the name it was created with in any case or spacing, which the ticket requires; the plan's own test asserting FindByName("ALPHA") returns null contradicts the ticket.
- pass 2 **DETECTS** `ticket-requirement-case-insensitive-lookup` /widgets/by-name/{name}: The by-name route passes the raw route value to FindByName without trimming or lower-casing it, so lookups by mixed-case or padded names will return 404 as required by the ticket's case- and whitespace-insensitive lookup.
- pass 2 **no** `compile-risk-widget-constructor` Widget: Adding a required third positional parameter breaks any existing two-argument `new Widget(...)` call sites, and the plan admits the grep for them was denied and the integration test file was not read, so the change may not compile.
- pass 3 **DETECTS** `ticket-requirement` WidgetStore.FindByName: The plan specifies a case-sensitive FindByName, which violates the epic requirement that a client can look a widget up by its creation name in any case or spacing, and the plan's own test asserts FindByName("ALPHA") returns null.
- pass 3 **no** `compile-risk` Widget: Adding a required CreatedAt parameter breaks any existing two-argument `new Widget(...)` call site, and the plan admits these call sites were not checked.
- pass 4 **DETECTS** `case-insensitive-lookup` WidgetStore.FindByName: FindByName is specified as case-sensitive and does not trim input, so a client that created "Alpha" (stored as "alpha") gets a 404 from /widgets/by-name/Alpha, which breaks the ticket's requirement to look up by the creation name in any case or spacing.
- pass 5 **DETECTS** `ticket-requirement-case-insensitive-lookup` WidgetStore.FindByName: The ticket requires a client to look up a widget by the name it was created with in any case or spacing, but this plan makes FindByName case-sensitive and does not normalise the query, so FindByName("ALPHA") returns null and the new by-name route cannot satisfy the epic.
- pass 5 **DETECTS** `ticket-requirement-case-insensitive-lookup` MapGet /widgets/by-name/{name}: The route passes the raw path segment to FindByName without trimming or lower-casing, so a lookup such as "  Alpha " will miss a widget stored as "alpha" even though the ticket requires any case or spacing to work.
- pass 5 **no** `unverified-compile-impact` Widget: Adding a required third parameter to Widget will break any existing two-argument `new Widget(...)` call sites, and the diff does not show that these were checked (the plan itself says the grep was denied and the integration test file was not read).

