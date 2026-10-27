# Cel.NET evaluation — Spike 1 go/no-go checks

Date: 2026-10-27. Spec: `docs/09-workflow-language.md` section 8 (Expressions),
especially 8.1, 8.2, 8.3, 8.4, 8.5.

Code: `spikes/01-interpreter/src/CelChecks` — a standalone .NET 9 console project,
not added to any solution file.

```
dotnet run -c Release --project spikes/01-interpreter/src/CelChecks -- api      # regenerate the API dump
dotnet run -c Release --project spikes/01-interpreter/src/CelChecks -- c1|c2|c3|c4|timing|pkg|probe|all
```

Raw captured output:

| File | Contents |
|---|---|
| `results/cel-checks-run.txt` | Full `all` run, Release build, 544 lines |
| `results/celnet-api-dump.txt` | Every public type and public member of `Cel.main` (370 types) |
| `results/probe.txt` | Env postures, stdlib contents, clock/randomness reachability |
| `results/timing-cold.txt` | Three fresh-process cold-start runs |
| `results/timing-release.txt` | Warm evaluation and warm compile, Release |
| `results/pkg.txt` | Resolved package facts and dependency graph |

Environment: .NET SDK 9.0.320, Windows, `Cel.NET` 2.3.1 from nuget.org.

---

## Package facts

| Fact | Value |
|---|---|
| NuGet id / version | `Cel.NET` **2.3.1** |
| Resolved assembly | `Cel.main`, Version `2.3.1.0` |
| Informational version | `2.3.1+9aea3698ea4565018d826a7cc6cc14f54408b5a6` |
| Target frameworks in the package | `net10.0`, `net8.0`, `netstandard2.1`, `net462` |
| Consumed by this project under | `net9.0`, resolved from the `net8.0` asset |
| Upstream | <https://github.com/rayokota/cel.net> |

Note against `docs/09` 8.1: the document says Cel.NET "targets `net8.0` and
`net10.0`". It actually ships **four** target frameworks — the two named plus
`netstandard2.1` and `net462`. A `net9.0` consumer resolves the `net8.0` asset,
which is fine, but the sentence in 8.1 is incomplete.

### Transitive dependencies

From `obj/project.assets.json`, target `net9.0` (8 packages, Cel.NET included):

```
Antlr4.Runtime.Standard/4.13.1           deps: (none)
Apache.Avro/1.12.1                       deps: Newtonsoft.Json, System.CodeDom
Cel.NET/2.3.1                            deps: Antlr4.Runtime.Standard, Apache.Avro, Google.Protobuf,
                                               Grpc.Core.Api, Newtonsoft.Json, NodaTime
Google.Protobuf/3.26.1                   deps: (none)
Grpc.Core.Api/2.63.0                     deps: (none)
Newtonsoft.Json/13.0.4                   deps: (none)
NodaTime/3.3.3                           deps: (none)
System.CodeDom/8.0.0                     deps: (none)   <- via Apache.Avro
```

**Seven transitive dependencies, six of them direct.** Confirmed loaded at runtime:

```
Antlr4.Runtime.Standard 4.13.1.0   Avro 1.12.0.0   Google.Protobuf 3.26.1.0
Grpc.Core.Api 2.0.0.0              Newtonsoft.Json 13.0.0.0   NodaTime 3.3.3.0
```

This is a real finding for `docs/09` 8.1's risk assessment. `Apache.Avro` and
`Grpc.Core.Api` are pulled in by an expression evaluator that does neither Avro
nor gRPC; `Google.Protobuf` and `Antlr4.Runtime.Standard` are inherent to a
CEL-Java port. `Grpc.Core.Api` and `Apache.Avro` are the two most likely to
collide with versions the Temporal .NET SDK or the artifact store already pins.

### Timing

Cold start, three fresh processes, Release build (from `results/timing-cold.txt`):

| Run | Env.NewCustomEnv | parse+check+plan | first Eval | **total to first evaluation** |
|---|---|---|---|---|
| 1 | 72.118 ms | 68.436 ms | 3.267 ms | **143.820 ms** |
| 2 | 68.241 ms | 64.749 ms | 2.945 ms | **135.934 ms** |
| 3 | 69.525 ms | 80.930 ms | 4.862 ms | **155.318 ms** |

Warm, same process, same expression
(`findings.blocking_count == 0 && size(run.repositories) > 1`):

| Operation | n | mean | p50 | p95 | p99 |
|---|---|---|---|---|---|
| `IProgram.Eval` (Env/Prog path) | 100,000 | **0.67–1.0 µs** | 0.50–0.90 µs | 0.70–1.20 µs | 1.20–3.50 µs |
| `Script.Execute` (ScriptHost path) | 100,000 | **0.99–1.5 µs** | 0.80–1.30 µs | 1.10–1.90 µs | 1.30–2.90 µs |
| parse + check + plan | 10,000 | **95–141 µs** | 47–142 µs | 217–253 µs | 286–345 µs |

Ranges span two runs: `results/timing-release.txt` (timing measured in isolation)
and `results/cel-checks-run.txt` (timing measured after checks 1–4 had warmed the
process further, which is the faster end).

The 136–155 ms cold start is one-off JIT plus static initialisation of the Antlr4
parser tables, Protobuf reflection and the standard-library overload tables. The
second compilation in the same process costs 2.0–3.2 ms, so a warm worker pays
about **2 ms to load an expression and under 2 µs to evaluate it**. Both are
negligible against a Temporal heartbeat, which is what 8.5 requires.

---

## Check 1 — Restricted checker

### Verdict: **PASS**

The checker rejects an unregistered function and an unknown variable name. It
rejects both with the same mechanism, at the same point, before evaluation.

### API surface

| Role | Type / member |
|---|---|
| Environment factory | `Cel.Env.NewCustomEnv(params EnvOption[])` / `NewCustomEnv(ITypeRegistry, IList<EnvOption>)` |
| Standard library opt-in | `Cel.LibraryOptions.StdLib()`, `Cel.LibraryOptions.Lib(ILibrary)` |
| Macro control | `Cel.EnvOptions.ClearMacros()` |
| Declarations | `Cel.EnvOptions.Declarations(IList<Decl>)` / `Declarations(params Decl[])` |
| Declaration builders | `Cel.Checker.Decls.NewVar`, `Decls.NewFunction`, `Decls.NewOverload`, `Decls.NewInstanceOverload`, `Decls.NewMapType`, `Decls.NewListType`, `Decls.NewTypeParamType` |
| The stdlib's own decls | `Cel.Checker.Checker.StandardDeclarations` (`IList<Decl>`, 55 entries) |
| Parse | `Cel.Env.Parse(string)` → `Env.AstIssuesTuple` |
| **The check itself** | `Cel.Env.Check(Ast)` → `Env.AstIssuesTuple` with `.HasIssues()` and `.Issues` |
| Checker internals | `Cel.Checker.Checker.Check(ParseResult, ISource, CheckerEnv)` → `Checker.CheckResult` with `.Errors` |
| Errors | `Cel.Issues.Errors` (`IList<CelError>`), `Cel.Common.CelError`, `Cel.Common.TypeErrors` |
| Convenience wrapper | `Cel.Tools.ScriptHost.BuildScript(...).WithDeclarations(...).Build()` throws `Cel.Tools.ScriptCreateException` (with an `.Issues` property) |

The rejection happens in `Env.Check`, which runs `Cel.Checker.Checker.Check`
against a `CheckerEnv` built from the declared `Decl` protos. `CheckerEnv` has
`LookupFunction(string)` and `LookupIdent(string)`; an unresolved name becomes
`CheckerEnv.Add` failing, or a `TypeErrors.UnknownType` / "undeclared reference"
error on the `Issues` list.

### Evidence

Posture C is the one the engine should adopt: filter Cel.NET's **own** stdlib
declaration protos down to the names 8.4 allows, add the four variable
namespaces from 8.3, and switch the comprehension macros off. It keeps 25 of 55
stdlib declarations.

Accepted, all from 8.3/8.4 (from `results/cel-checks-run.txt`, `1i-1..9`):

```
[1i-4 findings namespace]
  expression: findings.blocking_count > 0
  result:     ACCEPTED (parse+check+plan all passed)

[1i-5 size()]
  expression: size(run.repositories) > 1
  result:     ACCEPTED (parse+check+plan all passed)

[1i-8 matches()]
  expression: params.ticket_id.matches('^TICKET-[0-9]+$')
  result:     ACCEPTED (parse+check+plan all passed)
```

Rejected (from `1i-10..22`, trimmed):

```
[1i-18 'httpGet' (I/O)]
  expression: httpGet('https://example.com')
  result:     REJECTED
  exception:  System.InvalidOperationException
  message:    check: ERROR: <input>:1:8: undeclared reference to 'httpGet' (in container '') / | httpGet('https://example.com') / | .......^

[1i-19 'manifest' namespace (8.3 says it does not exist)]
  expression: manifest.repositories.size() > 0
  result:     REJECTED
  message:    check: ERROR: <input>:1:1: undeclared reference to 'manifest' (in container '') / | manifest.repositories.size() > 0 / | ^

[1i-20 comprehension 'all' (8.4: no comprehensions)]
  expression: run.repositories.all(r, r.startsWith('svc-'))
  result:     REJECTED
  message:    check: ERROR: <input>:1:21: undeclared reference to 'all' (in container '') / | run.repositories.all(r, r.startsWith('svc-')) / | ....................^ / ERROR: <input>:1:22: undeclared reference to 'r' (in container '')

[1i-12 'timestamp' (clock-adjacent)]
  expression: timestamp(0)
  result:     REJECTED
  message:    check: ERROR: <input>:1:10: undeclared reference to 'timestamp' (in container '') / | timestamp(0) / | .........^

[1i-16 'now' (a clock, if it existed)]
  expression: now()
  result:     REJECTED
  message:    check: ERROR: <input>:1:4: undeclared reference to 'now' (in container '') / | now() / | ...^
```

Via the `ScriptHost` convenience path the same rejection surfaces as a typed
exception rather than a returned `Issues` list (`1b`, `1d`):

```
[1b unregistered function 'httpGet']
  result:     REJECTED
  exception:  Cel.Tools.ScriptCreateException
  message:    check failed: ERROR: <input>:1:8: undeclared reference to 'httpGet' (in container '') / | httpGet('https://example.com') / | .......^
  issue:      Error{location=line=1, column=7, message='undeclared reference to 'httpGet' (in container '')'}

[1d unknown variable 'manifest']
  result:     REJECTED
  exception:  Cel.Tools.ScriptCreateException
  message:    check failed: ERROR: <input>:1:1: undeclared reference to 'manifest' (in container '') / | manifest.repositories.size() > 0 / | ^
```

The exact exception text the engine will see:

```
undeclared reference to '<name>' (in container '')
```

### Three findings the check surfaced

**1. The default posture is wide open.** `Env.NewEnv()` (and
`ScriptHost.NewBuilder().Build()`) registers the whole standard library *and* the
comprehension macros before you declare anything. Under that posture all of these
are accepted (`1c`, `1f`, `1g`, and `probe.txt`):

```
  size(run.repositories) > 1                  -> CHECK ACCEPT
  params.ticket_id.matches('^TICKET-[0-9]+$') -> CHECK ACCEPT
  run.repositories.all(r, r.startsWith('svc-')) -> CHECK ACCEPT   <- 8.4 forbids comprehensions
  timestamp('2026-01-01T00:00:00Z') > timestamp(0) -> CHECK ACCEPT <- not in 8.4
```

The engine must therefore use `Env.NewCustomEnv(...)` and opt the stdlib in
explicitly. `Env.NewEnv()` is not safe. This is a design constraint on the
engine, not a defect in Cel.NET — the whitelist is opt-in by construction.

**2. `lower` and `upper` do not exist.** 8.4 lists them. They are in neither the
core stdlib (`Checker.StandardDeclarations`) nor `Cel.Extension.StringsLib`
(`probe.txt`, posture D):

```
  lower('ABC')    -> REJECT: ERROR: <input>:1:6: undeclared reference to 'lower' (in container '')
  upper('abc')    -> REJECT: ERROR: <input>:1:6: undeclared reference to 'upper' (in container '')
  'ABC'.lower()   -> REJECT: ERROR: <input>:1:12: undeclared reference to 'lower' (in container '')
  'a,b'.split(',')   -> ACCEPT    <- StringsLib does work; it just has no lower/upper
```

`StringsLib` supplies `split`, `replace`, `trim`, `substring`, `join` and others,
but not the two 8.4 names. Section 8.4 needs correcting: either drop `lower` and
`upper`, or note that the engine must register them as custom functions. This is
a spec error, not a Cel.NET failure.

**3. No clock, randomness or I/O is reachable.** Confirmed by probing for the
obvious names under the full stdlib (`probe.txt`):

```
  now()          -> CHECK REJECT: undeclared reference to 'now'
  random()       -> CHECK REJECT: undeclared reference to 'random'
  rand()         -> CHECK REJECT: undeclared reference to 'rand'
  uuid()         -> CHECK REJECT: undeclared reference to 'uuid'
  currentTime()  -> CHECK REJECT: undeclared reference to 'currentTime'
  math.greatest(1, 2) -> CHECK REJECT: undeclared reference to 'math'
  'a'.format([1])     -> CHECK REJECT: undeclared reference to 'format'
```

`timestamp` and `duration` are the only time-related names, they are pure
conversions, and posture C removes them anyway. 8.5's "impossible" row holds.

---

## Check 2 — Replay determinism

### Verdict: **PASS**

No nondeterminism found, in any of the four forms tested.

### API surface

| Role | Type / member |
|---|---|
| Compile | `Cel.Env.Parse(string)` → `Env.Check(Ast)` → `Env.Program(Ast)` |
| Program | `Cel.IProgram.Eval(object vars)` → `Cel.EvalResult` |
| Activation | `Cel.Interpreter.IActivation`, `MapActivation`, `HierarchicalActivation`, `VarActivation` |
| Value | `Cel.Common.Types.Ref.IVal` (`.Value()`, `.ConvertToNative(Type)`) |
| Convenience | `Cel.Tools.Script.Execute<T>(IDictionary<string,object>)` |

### Evidence

Six expressions, 20,000 evaluations each, comparing every result against the
first by SHA-256 of its rendered form (from `results/cel-checks-run.txt`):

```
[findings.blocking_count > 0]
  iterations:            20000
  first value:           False (Boolean)
  distinct result hashes:1
  mismatches vs first:   0
  verdict:               DETERMINISTIC

[run.repositories[0] + '/' + run.branch]
  iterations:            20000
  first value:           svc-alpha/feature/TICKET-1234 (String)
  distinct result hashes:1
  mismatches vs first:   0
  verdict:               DETERMINISTIC

[params.ticket_id.startsWith('TICKET-') ? 'ticket' : 'other']
  first value:           ticket (String)
  distinct result hashes:1
  mismatches vs first:   0
  verdict:               DETERMINISTIC
```

All six: `distinct result hashes: 1`, `mismatches vs first: 0`, 20,000 iterations
each. 120,000 evaluations, one distinct result per expression.

Map insertion order, four separate tests:

```
-- map insertion order independence --        (64 keys, 7 insertion orders)
  order forward   keys=64  value=2016  hash=E9D7830C3AB84C13
  order reverse   keys=64  value=2016  hash=E9D7830C3AB84C13
  order shuffle#1 keys=64  value=2016  hash=E9D7830C3AB84C13
  order shuffle#2 keys=64  value=2016  hash=E9D7830C3AB84C13
  order shuffle#3 keys=64  value=2016  hash=E9D7830C3AB84C13
  order shuffle#4 keys=64  value=2016  hash=E9D7830C3AB84C13
  order shuffle#5 keys=64  value=2016  hash=E9D7830C3AB84C13
  distinct hashes across 7 insertion orders: 1
  verdict: ORDER-INDEPENDENT

-- map size() and key order --
  order[0] size(params) = 64     ... order[6] size(params) = 64
  distinct hashes: 1
  verdict: ORDER-INDEPENDENT

-- nested maps: 32 sub-maps, 7 insertion orders --   (inner keys shuffled too)
  order[0] value=192  hash=193B99A41D9BB026
  order[1] value=192  hash=193B99A41D9BB026
  ...
  order[6] value=192  hash=193B99A41D9BB026
  distinct hashes across 7 orders: 1
  verdict: ORDER-INDEPENDENT

-- map-valued result: source key order vs equality --
  {'a': 1, 'b': 2, 'c': 3, 'd': 4} -> {a=1, b=2, c=3, d=4}
  {'d': 4, 'c': 3, 'b': 2, 'a': 1} -> {a=1, b=2, c=3, d=4}
  {'b': 2, 'd': 4, 'a': 1, 'c': 3} -> {a=1, b=2, c=3, d=4}
  ({'a': 1, 'b': 2, 'c': 3, 'd': 4}) == ({'d': 4, 'c': 3, 'b': 2, 'a': 1}) == ({'b': 2, 'd': 4, 'a': 1, 'c': 3}) -> True
  distinct rendered results: 1
  verdict: ORDER-INDEPENDENT
```

Field selection is by name, never by position, so insertion order cannot leak.
The last test is the strongest: CEL's own map equality compares the three
differently-ordered literals as equal, so even a map-valued expression result is
order-canonical.

Caveat: this is 120,000 evaluations of six expressions on one machine. It is
strong evidence, not a proof. The structural argument is what carries the
conclusion — check 1 showed no clock, randomness or I/O is reachable, and CEL has
no loops or recursion.

---

## Check 3 — Static cost estimation

### Verdict: **PASS — it exists**

This was the uncertain one. It is there, it is public, and it is genuinely static.

### API surface

| Role | Type / member |
|---|---|
| **The estimate** | `Cel.Cel.EstimateCost(object p)` — static, returns `Cel.Interpreter.Cost` |
| Instance form | `Cel.Prog.Cost()` — `Cel.Prog : IProgram, ICoster` |
| The interface | `Cel.Interpreter.ICoster.Cost()` — implemented by `Cel.Prog` and by every `Eval*` interpretable node |
| The value type | `Cel.Interpreter.Cost` with `Int64 Min`, `Int64 Max`, `Cost.Add(Cost)`, `Cost.Multiply(long)`, `Cost.Of(long,long)` |
| Sentinels | `Cost.None`, `Cost.OneOne`, `Cost.Unknown`, `Cost.EstimateCost(object)` |
| Helpers | `Cel.Interpreter.InterpretableUtils.CalExhaustiveBinaryOpsCost`, `CalShortCircuitBinaryOpsCost`, `SumOfCost` |

From the reflected API dump (`results/celnet-api-dump.txt`), `grep -i cost`
returns 89 hits. The relevant block:

```
TYPE class Cel.Cel
  METHOD Cost EstimateCost(Object p) [static]

TYPE class Cel.Interpreter.Cost
  METHOD Cost Add(Cost c)
  METHOD Cost EstimateCost(Object i) [static]
  PROP Int64 Max
  PROP Int64 Min
  METHOD Cost Multiply(Int64 multiplier)
  FIELD Cost None [static]
  METHOD Cost Of(Int64 min, Int64 max) [static]
  FIELD Cost OneOne [static]
  FIELD Cost Unknown [static]

TYPE class Cel.Prog : IProgram, ICoster
  METHOD Cost Cost()

TYPE interface Cel.Interpreter.ICoster
  METHOD Cost Cost()
```

`ICoster` is implemented by 30 interpreter node types — `EvalAnd`, `EvalOr`,
`EvalBinary`, `EvalFold`, `EvalMap`, `EvalAttr`, `EvalWatch`, and so on — which is
how the estimate is built bottom-up over the planned interpretable tree.

### Evidence

```
-- 3a: cost surface present in the exported API --
  Cel.Cel                                PRESENT
  Cel.Interpreter.Cost                   PRESENT
  Cel.Interpreter.ICoster                PRESENT
  Cel.Prog                               PRESENT
  Cel.Interpreter.InterpretableUtils     PRESENT
  Cost.Min/Max properties: Int64 Min, Int64 Max
  Cost.None = Cost{min=0, max=0}; Cost.Unknown = Cost{min=0, max=9223372036854775807}; Cost.OneOne = Cost{min=1, max=1}
  Cel.Cel.EstimateCost(object) static: Cel.Interpreter.Cost EstimateCost(System.Object)

-- 3b: measured cost estimates (static, at compile time) --
  findings.blocking_count > 0
      len=  27  Prog.Cost() min=     2 max=     2   Cel.EstimateCost() min=     2 max=     2
  size(run.repositories) > 1
      len=  26  Prog.Cost() min=     3 max=     3   Cel.EstimateCost() min=     3 max=     3
  params.ticket_id.startsWith('TICKET-')
      len=  38  Prog.Cost() min=     2 max=     2   Cel.EstimateCost() min=     2 max=     2
  1 + 1
      len=   5  Prog.Cost() min=     1 max=     1   Cel.EstimateCost() min=     1 max=     1
  run.repositories[0] + '/' + run.branch
      len=  38  Prog.Cost() min=     4 max=     4   Cel.EstimateCost() min=     4 max=     4
  findings.blocking_count == 0 && findings.open_count > 3 && size(run...
      len=  85  Prog.Cost() min=     2 max=     9   Cel.EstimateCost() min=     2 max=     9
  params.k00 + params.k01 + params.k02 + params.k03 + params.k04 + pa...
      len= 777  Prog.Cost() min=   119 max=   119   Cel.EstimateCost() min=   119 max=   119
```

`Prog.Cost()` and `Cel.Cel.EstimateCost(prog)` agree on every case, as expected —
the static helper just dispatches to `ICoster`.

It is genuinely static: the value is read off the planned interpretable tree after
`Env.Program(ast)` and before any `Eval` call, with no activation and no input
data. `Min` and `Max` diverge only where short-circuiting is possible — the
conjunction above is `min=2 max=9` because `&&` may stop early. That is the
correct semantics: `Max` is the worst case, and that is the number a budget must
use.

`Cost.Unknown` is `min=0, max=Int64.MaxValue`. An engine budget must treat
`Max == Int64.MaxValue` as a rejection, since no finite budget can be compared
against it.

---

## Check 4 — Cost limit enforceable

### Verdict: **PASS** (primary control), with one negative

Because check 3 found a real estimate, the primary control is available. There is
no built-in budget parameter, so the engine applies the limit itself; the 8.5 caps
were also tested and work.

### API surface

| Role | Type / member |
|---|---|
| The budget comparison | `Cel.Cel.EstimateCost(program).Max` compared against a constant |
| The program | `Cel.Env.Program(Ast, params ProgramOption[])` → `IProgram` (a `Cel.Prog`) |
| AST for node counting | `Cel.Ast.Expr` → `Google.Api.Expr.V1Alpha1.Expr` with `ExprKindOneofCase` (`ConstExpr`, `IdentExpr`, `SelectExpr`, `CallExpr`, `ListExpr`, `StructExpr`, `ComprehensionExpr`) |
| Parser-side limit (unused) | `Cel.Parser.Options.ExpressionSizeCodePointLimit`, `MaxRecursionDepth`, `ErrorRecoveryLimit` |
| Runtime observer (**does not work**) | `Cel.Interpreter.InterpretableDecoratorUtils.DecObserveEval(EvalObserver)`, `Cel.ProgramOptions.CustomDecorator(InterpretableDecorator)` |

### Evidence — 4a: no built-in budget knob

Reflecting over all 370 exported types for members matching Cost/Budget/Limit
returns 48 hits. Every one is either a cost *reader* (`Cost()`, `EstimateCost`,
`SumOfCost`) or a *parser* limit. None is a "reject when over budget" parameter:

```
  members matching Cost/Budget/Limit: 48
    Cel.Cel.EstimateCost
    Cel.Interpreter.Cost.EstimateCost
    Cel.Interpreter.ICoster.Cost
    Cel.Prog.Cost
    Cel.Interpreter.InterpretableUtils.CalExhaustiveBinaryOpsCost
    Cel.Interpreter.InterpretableUtils.CalShortCircuitBinaryOpsCost
    Cel.Interpreter.InterpretableUtils.SumOfCost
    Cel.Parser.Options.ErrorRecoveryLimit
    Cel.Parser.Options.ExpressionSizeCodePointLimit
    Cel.Parser.Options+Builder.ExpressionSizeCodePointLimit
    ... (38 further ICoster.Cost implementations)
  -> none of these is a 'reject when over budget' knob; the limit must be
     applied by the engine against Cost.Max. Demonstrated in 4b.
```

The limit is therefore engine code — one comparison — not a library feature. That
is acceptable; it is three lines, and it runs at load time where 8.5 wants it.

### Evidence — 4b: the cost limit enforced

Budget: `Cost.Max <= 100`.

```
-- 4b: engine-enforced cost limit, using Cel.Cel.EstimateCost --
  budget: Cost.Max <= 100

  [small guard]  len=27
      Cost = min=2 max=2   AST nodes = 5
      gate: ACCEPTED -- cost max 2 <= budget 100

  [typical route guard]  len=54
      Cost = min=3 max=6   AST nodes = 11
      gate: ACCEPTED -- cost max 6 <= budget 100

  [40-term concatenation]  len=517
      Cost = min=79 max=79   AST nodes = 120
      gate: ACCEPTED -- cost max 79 <= budget 100

  [400-term concatenation]  len=5597
      Cost = min=799 max=799   AST nodes = 1200
      gate: REJECTED -- cost max 799 > budget 100

  [300-conjunction guard]  len=6486
      Cost = min=2 max=899   AST nodes = 1500
      gate: REJECTED -- cost max 899 > budget 100
```

Over-budget expressions are rejected at load time, before any evaluation. Real
route guards from `docs/13` land at cost 2–6, three orders of magnitude below any
sane budget.

### Evidence — 4c: the 8.5 caps as defence in depth

The two 8.5 caps — 1000 characters, 200 AST nodes — also work, and are enforced
at load time on the source text and the checked AST:

```
-- 4c: 8.5 caps -- length <= 1000 chars, AST nodes <= 200 --

  expression                                      len  nodes  gate
  --------------------------------------------------------------------------------------------
  findings.blocking_count > 0                       27      5  ACCEPTED
  size(run.repositories) > 1 && findings.op...      54     11  ACCEPTED
  params.k00 + params.k01 + params.k02 + pa...     777    180  ACCEPTED
  params.k00 + params.k01 + params.k02 + pa...    1037    240  REJECTED: length 1037 > 1000
  params.k00 == 0 && params.k01 == 1 && par...     786    200  ACCEPTED
  params.k000 + params.k001 + params.k002 +...    5597   1200  REJECTED: length 5597 > 1000
```

The 1000-character cap binds first in every case tested — 1000 characters of CEL
cannot reach 200 nodes for the expression shapes above (777 chars → 180 nodes).
So the node cap is effectively dormant, which is fine: it is the backstop for the
dense-expression case the length cap misses.

### Negative — 4d: no runtime interrupt

There is no way to abort a running evaluation. `DecObserveEval` fires, but
Cel.NET catches whatever the observer throws:

```
-- 4d: runtime step ceiling via ProgramOptions.CustomDecorator(DecObserveEval(...)) --
  expression: 400-term concatenation (len=5597)
  step ceiling: 50
  observer fired 1199 times; ceiling tripped: True
  Eval() returned normally: type=System.String len=530
  -> the observer CANNOT abort evaluation: Cel.NET catches exceptions thrown
     from the EvalObserver delegate and evaluation completes anyway.
     A runtime step ceiling is therefore NOT available via this surface.
     The static cost estimate in 4b is the only working enforcement point.
```

The observer fired 1,199 times and the ceiling tripped, yet `Eval` returned a
complete 530-character result. This does not fail the check — 8.5 asks for a
**cost** limit at check time, and 4b provides one — but it means the engine has no
belt-and-braces stop once evaluation starts. The static estimate is the only
enforcement point, so it is load-bearing rather than merely defensive. The
mitigation is that a cost budget of 100 bounds evaluation to microseconds, so
there is nothing meaningful to interrupt.

---

## Verdict

**Cel.NET stays.** All four checks pass. The restricted checker rejects
unregistered functions and unknown variables with `undeclared reference to '<name>'
(in container '')` at `Env.Check` time (check 1: PASS). Evaluation is
deterministic across 120,000 repeated runs and is wholly independent of map
insertion order in all four forms tested (check 2: PASS). Static cost estimation
**exists** — `Cel.Cel.EstimateCost(object)` and `Cel.Prog.Cost()` return a
`Cel.Interpreter.Cost` with `Min`/`Max`, computed from the planned interpretable
tree with no input data (check 3: PASS — the uncertain one, and it resolved
positively). A budget is enforceable: there is no built-in knob, but comparing
`Cost.Max` against a constant at load time rejects over-budget expressions, and
the 8.5 caps of 1000 characters and 200 AST nodes work as defence in depth
(check 4: PASS). Nothing failed, so the hand-written-subset fallback is not
triggered, and `docs/09` 8.1's "if it does not" branch does not apply. Three
things do need to change in the spec rather than in the choice of library: 8.4
lists `lower` and `upper`, which Cel.NET does not declare anywhere (the engine
must register them or the spec must drop them); 8.1's target-framework sentence
is incomplete (four TFMs ship, not two); and the engine must build its environment
with `Env.NewCustomEnv` and an explicit stdlib opt-in, because the default
`Env.NewEnv()` registers the comprehensions and the whole stdlib before anything
is declared, which 8.4 forbids. The seven-package transitive dependency set —
`Apache.Avro` and `Grpc.Core.Api` in particular — is worth recording against 8.1's
maintainer-risk row, since those two are the likeliest to collide with versions
the Temporal SDK already pins.
