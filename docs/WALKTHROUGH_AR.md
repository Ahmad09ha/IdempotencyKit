# IdempotencyKit من الصفر: كيف طلب واحد ما بينحفظ مرتين؟

أحمد، إذا بدك تفهم المشروع من الكود بدل ما تحفظ أسماء الحزم، امشِ مع طلب واحد: زبون ضغط «اطلب»، السيرفر حفظ الطلب، بس الاتصال انقطع قبل ما يرجع رقم الطلب. من جهة الزبون في timeout. من جهة قاعدة البيانات الطلب ممكن يكون انحفظ فعلاً. هون بلّشت فكرة IdempotencyKit.

## 1. من وين بيجي المفتاح؟

العميل بيولّد مفتاح للعملية قبل أول إرسال وبيحتفظ فيه. إذا صار timeout، بيرجع يبعت **نفس المفتاح ونفس الطلب**. إذا الزبون قصد يعمل طلب تاني، بيستخدم مفتاح جديد.

```http
POST /orders HTTP/1.1
Authorization: Bearer ...
Content-Type: application/json
Idempotency-Key: demo-order-1

{"productId":42,"quantity":2}
```

المفتاح بحدّ ذاته ما بيثبت مين الزبون، وما بيقول إذا محتوى الطلب تغيّر. لذلك المكتبة بتحسب شيئين مختلفين:

| شو بنحسب؟ | من شو؟ | لِشو؟ |
| --- | --- | --- |
| `scopeKeyHash` | namespace للتطبيق + operation ID ثابت مثل `orders.create.v1` + tenant إن وجد + هوية المستخدم الموثوقة + المفتاح | لنلاقي سجل العملية الصح. لو شخصين استخدموا نفس المفتاح، كل واحد إلو سجله. |
| `requestFingerprint` | HTTP method + operation ID + path + query + الـ JSON الأصلي | لنعرف إذا المفتاح نفسه رجع لنفس الطلب أو صار في تغيير بالمعنى. |

الاتنين SHA-256 بصيغة `v1:…`. النسختين بتستخدموا نفس قواعد ترتيب خصائص الـ JSON وتطبيع الأرقام. تبديل ترتيب `productId` و`quantity` ما بيغيّر البصمة. تغيير `quantity` من 2 لـ 3 بيغيّرها. ترتيب عناصر الـ arrays ضلّ مهم، وبـ query بينحفظ تكرار القيم بس مو ترتيبها. بصمة الطلب لازم تُحسب من النص الأصلي، لا من إعادة تحويل جسم الطلب لـ JSON بعد ما JavaScript يكون قرّب رقم كبير. الـ hash مو توقيع، ومو بديل عن التحقق من الهوية والصلاحيات.

**افتح أول شي:** [.NET: RequestIdentityHasher](../dotnet/src/IdempotencyKit.Core/RequestIdentityHasher.cs) و[RequestFingerprinter](../dotnet/src/IdempotencyKit.Core/RequestFingerprinter.cs)، وبعدين [TypeScript: request-identity](../typescript/packages/core/src/request-identity.ts) و[request-fingerprinter](../typescript/packages/core/src/request-fingerprinter.ts). [مواصفات v1](../spec/IDENTITY_AND_FINGERPRINT.md) بتشرح بالضبط ليش طريقة كتابة الـ JSON الموحّدة هون إلها قواعدها الخاصة.

## 2. كيف بيوصل الطلب لهون؟

بـ **ASP.NET Core** التطبيق بيعمل authentication وauthorization، وبعدين `IdempotencyMiddleware` بيقرأ إعدادات الـ route والـ JSON قبل الـ model binding. بتسجّل الإعدادات بـ `AddIdempotencyKit`، وبتحدد الـ endpoints اللي بدك تحميها بـ `.WithIdempotency(...)` أو `[Idempotent(...)]` وبتشغّل الـ middleware بـ `UseIdempotencyKit`.

بـ **NestJS Express** الـ guard هو اللي بيتحقق من المستخدم؛ بعده `IdempotencyInterceptor` بيشوف `@Idempotent(...)` اللي سجّلته على الـ route. `IdempotencyKitModule.forRoot(...)` بياخد الـ store ودالة تحديد هوية المستخدم الموثوقة. لما تعمل `NestFactory.create(AppModule, { rawBody: true })` بيضلّ الـ JSON الأصلي متاح للبصمة.

بالطريقتين الـ scope لازم يطلع من هوية موثوقة، مو من header بيقدر العميل يختلقه. قبل الـ replay كمان لازم التطبيق يتحقق إنو هالمستخدم لسه مسموح إلو يشوف النتيجة. الـ Transactional HTTP adapters فيها نقاط واضحة لإضافة تحقق من صلاحية العملية قبل أي claim أو replay.

**افتح:** [.NET middleware](../dotnet/src/IdempotencyKit.AspNetCore/IdempotencyMiddleware.cs) و[NestJS interceptor](../typescript/packages/nestjs/src/idempotency.interceptor.ts).

## 3. Standard mode: مين بيشتغل ومين بينطر؟

لما يجي أول طلب، الـ store بيحاول يحجز سجل للعملية بشكل ذرّي: مو `SELECT` وبعده `INSERT` بلا حماية، لأن طلبين ممكن يشوفوا السجل فاضي سوا. قيد التفرد والتنسيق بقاعدة البيانات بيحددوا مين أخذ الحجز. السجل بيكون `processing` ومعه تذكرة ملكية `ownerToken` ومدة حجز `lease`. المالك الصحيح، ضمن مهلة الحجز، هو اللي بيقدر يكمّل السجل أو يمدد المهلة. لما الـ handler يرجع نتيجة JSON مدعومة، المكتبة بتحفظ status وcontent type والـ body وهيدرز محددة مثل `Location`، وبتعلّم السجل `completed`.

إذا وصل retry:

| الحالة | شو بيصير؟ |
| --- | --- |
| نفس الـ scope ونفس البصمة والنتيجة بعدها محفوظة | replay؛ بدون تشغيل الـ handler مرة تانية. |
| نفس الـ scope والمفتاح بس بصمة غير | `422`؛ المفتاح مستعمل لطلب تاني. |
| نفس العملية مع حجز معروف وقيد التنفيذ | `409`؛ ممكن يرجع `Retry-After`. |
| الحجز خلص أو النتيجة غير معروفة | `409` بحالة `uncertain`؛ ما منعيد التنفيذ عالعمياني. |
| التخزين غير متاح | `503`؛ ما منشغّل العملية بدون حماية. |

وين المشكلة؟ الـ handler ممكن يعمل commit للطلب بتعامله الخاص، وبعدين البرنامج يوقف **قبل** ما يسجّل النتيجة بمخزن IdempotencyKit. الـ lease إذا انتهت بتخبرنا إنو وقت الحجز خلص، ما بتخبرنا إذا الطلب انحفظ. هون لازم reconciliation: نطابق نتيجة العملية من بيانات الدومين الموثوقة قبل ما نقرر شو الخطوة الآمنة. ما منمسح سجل `uncertain` كرمال نفرض إعادة المحاولة.

**افتح:** [عقد IIdempotencyStore](../dotnet/src/IdempotencyKit.Core/IIdempotencyStore.cs) وبالمقابل [TypeScript IdempotencyStore](../typescript/packages/core/src/idempotency-store.ts)، بعدين [مثال PostgreSQL store](../dotnet/src/IdempotencyKit.Postgres/PostgresIdempotencyStore.cs). الـ middleware والـ interceptor بياخدوا قرارات الـ store وبيترجموها لـ HTTP.

## 4. Transactional mode: ليش ما عاد في فجوة بين الحفظين؟

هون بدل ما نركّب decorator ونترك كل طبقة تعمل commit لحالها، منمرّر callback لمُنفّذ الـ transaction. الترتيب:

```text
BEGIN
  1. احجز مفتاح الطلب أو افحص النتيجة المحفوظة
  2. إذا جديد، نفّذ INSERT للطلب باستخدام نفس transaction
  3. جهّز وتحقق من الـ response واحفظه بنفس transaction
COMMIT
  4. ابعت الـ HTTP response للعميل
```

إذا البرنامج وقف **قبل** الـ commit، لا سطر الطلب ولا سجل النتيجة صار نهائي، فالـ retry بنفس المفتاح بيقدر ينفّذ. إذا وقف **بعد** الـ commit وقبل الرد، السجلين محفوظين، والـ retry بنفس المفتاح بيرجع `201` ونفس رقم الطلب بدل ما يعمل طلب جديد. وإذا جواب الـ commit نفسه ضاع، ما منخمّن النجاح ولا الفشل؛ العميل بيرجع **بنفس المفتاح** لنفس قاعدة البيانات المرجعية.

المهم تستخدم **نفس الـ connection ونفس الـ transaction** اللي عطاك ياها المنفّذ. إذا الـ repository فتح connection جديد أو كتب لقاعدة تانية، هالضمان ما بيشمل شغله. محوّلات EF Core 10 بتربط `DbContext` بنفس الـ connection وبتسجّله بالـ transaction بـ `UseTransactionAsync`. حتى connection تاني على **نفس ملف SQLite** ما بيكفّي. بـ Node SQLite، الـ callback بياخد واجهة لتنفيذ statements بدل ما يتحكّم بالـ commit بنفسه.

**افتح:** [.NET: ITransactionalIdempotencyExecutor](../dotnet/src/IdempotencyKit.Core/ITransactionalIdempotencyExecutor.cs) و[EF Core SQLite bridge](../dotnet/src/IdempotencyKit.EntityFrameworkCore.Sqlite/SqliteEfCoreTransactionalExecutor.cs)، أو [TypeScript contract](../typescript/packages/core/src/transactional-executor.ts) و[SQLite executor](../typescript/packages/sqlite/src/sqlite-transactional-executor.ts).

النقاط اللي بتستدعيها من الـ HTTP بتختلف حسب provider:

| قاعدة البيانات | ASP.NET Core | NestJS Express |
| --- | --- | --- |
| PostgreSQL | `AddPostgresTransactionalIdempotency` ثم `MapTransactionalPost` | `PostgresTransactionalHttpModule.forRoot` و`PostgresTransactionalHttpService.handleExpress` |
| SQL Server | `AddSqlServerTransactionalIdempotency` ثم `MapSqlServerTransactionalPost` | `SqlServerTransactionalHttpModule.forRoot` و`SqlServerTransactionalHttpService.handleExpress` |
| SQLite | `AddSqliteTransactionalIdempotency` ثم `MapSqliteTransactionalPost` | `SqliteTransactionalHttpModule.forRoot` و`SqliteTransactionalHttpService.handleExpress` |

لا تركّب `[Idempotent]` أو `@Idempotent` فوق route معمول Transactional. هدول مدخل الـ Standard mode؛ ما بيحوّلوا الـ handler لعملية ذرّية.

## 5. ليش في كود منفصل لكل قاعدة بيانات؟

الـ **Core** بيعرف معنى scope وclaim وreplay. الـ provider هو اللي بيعرف كيف يحقق هالسلوك على محركه. لذلك عنا نفس الفكرة، بس migrations وتنفيذ واختبارات مختلفة:

- **PostgreSQL:** قيد فريد على المفتاح وعمليات claim ذرّية. .NET بياخد `Npgsql`، وNode بياخد `pg`. افتح [migration](../spec/sql/postgres/001_idempotencykit_records.sql) و[Transactional executor](../dotnet/src/IdempotencyKit.Postgres/PostgresTransactionalExecutor.cs).
- **SQL Server:** منسّق لكل مفتاح بـ `sp_getapplock` مربوط بالـ transaction، بالإضافة لقيد التفرد. الـ CI بيختبر SQL Server 2022 Developer. افتح [migration](../spec/sql/sqlserver/001_idempotencykit_records.sql) و[Transactional executor](../dotnet/src/IdempotencyKit.SqlServer/SqlServerTransactionalExecutor.cs).
- **SQLite:** ملف حقيقي محلي على جهاز واحد، مع WAL و`synchronous=FULL` و`BEGIN IMMEDIATE`. SQLite عنده كاتب واحد على الملف؛ كاتب لعملية مختلفة ممكن يعطّلنا مؤقتاً. هاد خطأ storage `503`، مو دليل إنو المفتاح نفسه قيد التنفيذ `409`. افتح [migration](../spec/sql/sqlite/001_idempotencykit_records.sql) و[Transactional executor](../dotnet/src/IdempotencyKit.Sqlite/SqliteTransactionalExecutor.cs). ما منشارك ملف WAL عبر NFS/SMB بين أجهزة.

الـ migrations بتعمل جدول `idempotencykit_records`، غير جدول الطلبات تبع تطبيقك. قبل استخدام المكتبة، طبّق migration القاعدة المناسبة. بـ SQLite في `SqliteIdempotencyDatabase.InitializeAsync` بـ .NET و`initializeSqliteDatabase` بـ Node لتهيئة الملف والمخطط. بتطبيقك رح تضلّ بحاجة لجدول `orders` ومخططه وأي قواعد uniqueness خاصة بالشغل.

## 6. كيف اتبنى الـ repo، وبشو منثبت إنو شغّال؟

إقرا التاريخ مو بس لحتى تشوف كم ملف انضاف، لحتى تعرف شو السؤال اللي جاوب عليه كل PR:

1. المواصفات وبنية الحل: [spec](../spec) حدّدت سلوك واحد؛ [.NET solution](../dotnet/IdempotencyKit.slnx) و[npm workspace](../typescript/package.json) خلّوا التنفيذين قابلين للبناء كل واحد لحاله.
2. [#1](https://github.com/Ahmad09ha/IdempotencyKit/pull/1): نفس الطلب لازم يعطي نفس البصمة باللغتين. الـ [.NET tests](../dotnet/tests/IdempotencyKit.Core.Tests/FingerprintV1Tests.cs) و[Node tests](../typescript/tests/fingerprint-v1.test.mjs) بيشغّلوا [نفس ملف الأمثلة](../spec/test-vectors/fingerprint-v1.json).
3. [#2](https://github.com/Ahmad09ha/IdempotencyKit/pull/2) و[#3](https://github.com/Ahmad09ha/IdempotencyKit/pull/3): صار في store حقيقي بـ PostgreSQL، وبعده دخلنا على HTTP بـ Standard mode.
4. [#4](https://github.com/Ahmad09ha/IdempotencyKit/pull/4) و[#5](https://github.com/Ahmad09ha/IdempotencyKit/pull/5): سكّرنا الفجوة بين كتابة الطلب والـ response بالـ transaction، ووصلناها لـ EF Core وHTTP بشكل صريح.
5. [#6](https://github.com/Ahmad09ha/IdempotencyKit/pull/6): فصلنا العقد عن PostgreSQL. [ملف النتائج المشترك](../spec/fixtures/transactional-provider-v1.json) بيساعدنا نقارن معنى التنفيذ والـ replay والـ mismatch بين التنفيذين. توافق العقد ما بيعني إنو اختبرنا تشغيل التطبيقين بنفس الوقت على نفس قاعدة البيانات.
6. [#7](https://github.com/Ahmad09ha/IdempotencyKit/pull/7) و[#8](https://github.com/Ahmad09ha/IdempotencyKit/pull/8): ضفنا SQL Server core، وبعدها EF Core وHTTP. [#9](https://github.com/Ahmad09ha/IdempotencyKit/pull/9) و[#10](https://github.com/Ahmad09ha/IdempotencyKit/pull/10): عملنا نفس المراحل لـ SQLite على ملف فعلي بدل قاعدة بالذاكرة.
7. [GitHub Actions](../.github/workflows) بتشغّل مسارات منفصلة لـ [PostgreSQL](../.github/workflows/scaffold-ci.yml) و[SQL Server](../.github/workflows/sqlserver-ci.yml) و[SQLite](../.github/workflows/sqlite-ci.yml). [سجل الأدلة](VERIFICATION.md) فيه الـ runs، الاختبارات اللي مرقت، والسيناريوهات اللي ما انثبتت بعد. وقت تشوف crash test، اسأل: مات process حقيقي أو اختبرنا exception؟ مات قبل الـ commit أو بعده؟ والـ retry عدّ السجلات فعلاً أو بس شاف `201`؟

## 7. إمتى هالضمان ما بيمشي؟

- الـ transaction ما بتقدر ترجع إيميل انبعت، دفعة صارت عند بوابة خارجية، أو كتابة لقاعدة تانية. هني بحاجة لتصميم منفصل، مثل outbox للرسائل أو مفتاح مستقل لدى بوابة الدفع.
- ما في deduplication للأبد. بعد انتهاء صلاحية نتيجة مكتملة ممكن نفس المفتاح يُعامل كطلب جديد؛ إذا الدومين بده يمنع وجود طلبين لنفس checkout، حط قيد uniqueness مستقل بالدومين.
- النتائج المدعومة هي HTTP JSON محدودة، مو stream أو ملف أو `Set-Cookie`. ما تعتمد على تطابق كل byte على السلك عبر كل adapter: Standard في NestJS بيرجّع قيمة JSON حتى Nest يكتبها، بينما الـ Transactional Express adapters بيرجعوا الـ bytes المحفوظة مباشرة.
- دعم محرك ببيئة اختبار معينة مو شهادة production لكل طريقة نشر، أو ضمان لفشل Azure SQL أو النسخ الاحتياطي أو الـ failover. راجع [حدود الاختبارات](VERIFICATION.md) و[خطط قواعد البيانات](DATABASE_PROVIDER_ROADMAP.md).

## 8. جرّب بنفسك، وبعدين ارجع للكود

شغّل **واحد** من المثالين على جهازك. كل واحد إله ملف SQLite خاص فيه. المثال بيشتغل محلياً؛ `orders-demo` رمز ثابت للتعلّم فقط، مو JWT ولا طريقة توثيق مناسبة للإنتاج.

```bash
# من جذر الريبو، .NET 10
dotnet run --project dotnet/samples/Orders.AspNetCore

# أو من جذر الريبو، Node 24
cd typescript
npm ci
npm run start:orders
```

بـ terminal تاني، خليك على `5050` لـ .NET أو غيّره لـ `3000` لـ NestJS. استخدم مفتاح جديد إذا جرّبت من قبل:

```bash
base=http://127.0.0.1:5050
key=demo-order-1

curl -sS -H 'Authorization: Bearer orders-demo' "$base/orders"
for body in '{"productId":42,"quantity":2}' '{"quantity":2,"productId":42}' '{"productId":42,"quantity":3}'; do
  curl -i -X POST "$base/orders" \
    -H 'Authorization: Bearer orders-demo' \
    -H 'Content-Type: application/json' \
    -H "Idempotency-Key: $key" \
    --data "$body"
  printf '\n'
done
curl -sS -H 'Authorization: Bearer orders-demo' "$base/orders"
```

اللي لازم تشوفه: أول طلب `201`، التاني `201` بنفس الـ ID والـ `Location`، التالت `422`. وعدّاد الطلبات لازم يزيد **واحد** بس. هاد الجزء الأخير هو الدليل إنو الطلب ما تنفّذ مرتين؛ تشابه الردود لحاله ما بكفّي. اقرأ [ASP.NET Core sample](../dotnet/samples/Orders.AspNetCore/Program.cs) أو [NestJS controller](../typescript/apps/orders-api/src/app.controller.ts) وشوف وين بيستخدم الـ callback الاتصال أو الجلسة من المكتبة بدل ما يفتح واحد لحاله.

## 9. شو انضاف لتجهيز الإصدار؟

- **مراقبة التشغيل:** الـ HTTP adapters بتسجّل مرحلة مثل `standard.claim` أو `transactional.execute`، النتيجة مثل `replayed`، والمدة. بـ .NET افتح [IdempotencyTelemetry](../dotnet/src/IdempotencyKit.Core/IdempotencyTelemetry.cs)، وبـ Node افتح [subscribeIdempotencyTelemetry](../typescript/packages/core/src/telemetry.ts). أحداث المراقبة ما فيها المفتاح أو هوية المستخدم أو جسم الطلب؛ هي بتساعدك تشوف كثرة conflicts أو errors، وما بتثبت إنو الرد وصل.
- **تشغيل وتنظيف:** التطبيق هو اللي بيقرر موعد cleanup وبيستدعي `PurgeExpiredCompletedAsync` أو `purgeExpiredCompleted` على دفعات. هالعملية بتستهدف سجلات `completed` المنتهية فقط؛ السجل `uncertain` إلو خطوات تحقق منفصلة بـ [دليل التشغيل](OPERATIONS.md).
- **اختبارات SQLite إضافية:** اقرأ [اختبار إيقاف عملية ASP.NET Core](../dotnet/tests/IdempotencyKit.Sqlite.Tests/SqliteHttpProcessCrashTests.cs) و[اختبار ملف مشترك بين .NET وNode](../typescript/tests/sqlite-interop/shared-file.test.mjs). الأول بيختبر حدود الـ commit على process فعلي؛ الثاني بيختبر إذا نتيجة مخزّنة من تنفيذ بتنعاد من التنفيذ التاني على نفس الملف، وشو بيصير مع كاتب ماسك الملف. النتائج والبيئات المؤكدة مكانها [سجل الأدلة](VERIFICATION.md).
- **معاينة الحزم:** [Package Preview](../.github/workflows/package-preview-ci.yml) بيبني ملفات توزيع ويتحقق منها من دون نشرها. وجود build ناجح بالـ monorepo مو نفس ضمان إنو المكتبة بتتثبّت بمشروع مستقل؛ لهيك منفصلين build وفحص الحزم.

وقبل النشر فرّق بين **بناء الحزمة** و**رفعها**: `dotnet pack` بيطلع `.nupkg` و`npm pack` بيطلع `.tgz`، وبتقدر تجرّب تثبيتهم بمشروع تاني بدون رفعهم. خطوات التحقق والنشر لحزم هالريبو تحديداً بـ [دليل الإصدار](RELEASING.md).
