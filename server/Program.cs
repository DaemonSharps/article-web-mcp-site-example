using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Keep an anchor connection open for the process. DbContexts use their own
// connections to this named shared in-memory database.
const string SharedConnectionString = "Data Source=SignalStore;Mode=Memory;Cache=Shared";
var sqliteAnchorConnection = new SqliteConnection(SharedConnectionString);
sqliteAnchorConnection.Open();
builder.Services.AddSingleton(sqliteAnchorConnection);
builder.Services.AddDbContext<StoreDbContext>((_, options) =>
    options.UseSqlite(SharedConnectionString));
builder.Services.AddSingleton<AgentRuntime>();
builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 5080));

var app = builder.Build();
app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    var exception = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
    context.Response.StatusCode = exception is DbUpdateException ? StatusCodes.Status409Conflict : StatusCodes.Status500InternalServerError;
    await context.Response.WriteAsJsonAsync(new
    {
        error = exception is DbUpdateException ? "The requested change conflicts with current inventory or stored data" : "Internal server error"
    });
}));
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    await next();
});

using (var scope = app.Services.CreateScope())
{
    _ = scope.ServiceProvider.GetRequiredService<SqliteConnection>();
    var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
    await db.Database.EnsureCreatedAsync();
    await SeedData.InitializeAsync(db);
}

app.Lifetime.ApplicationStopping.Register(sqliteAnchorConnection.Dispose);

app.MapGet("/api/products", async (string? status, StoreDbContext db) =>
{
    var selectedStatus = string.IsNullOrWhiteSpace(status) ? "published" : status.Trim().ToLowerInvariant();
    if (selectedStatus != "all" && !StoreConstants.ProductStatuses.Contains(selectedStatus))
        return BadRequest("status must be all, draft, published, or archived");

    var query = db.Products.AsNoTracking().AsQueryable();
    if (selectedStatus != "all")
        query = query.Where(product => product.Status == selectedStatus);

    var products = await query.OrderBy(product => product.Name).ToListAsync();
    return Results.Ok(products.Select(ProductDto.FromEntity).ToList());
});

app.MapPost("/api/products", async (ProductWriteDto? input, StoreDbContext db) =>
{
    var validation = ProductValidation.Validate(input, creating: true);
    if (validation is not null)
        return BadRequest(validation);

    await StoreConstants.MutationGate.WaitAsync();
    try
    {
        var product = input!.ToEntity();
        if (await db.Products.AnyAsync(existing => existing.Id == product.Id))
            return Results.Conflict(new { error = "A product with this id already exists" });

        db.Products.Add(product);
        await db.SaveChangesAsync();
        return Results.Created($"/api/products/{product.Id}", ProductDto.FromEntity(product));
    }
    finally
    {
        StoreConstants.MutationGate.Release();
    }
});

app.MapPut("/api/products/{id}", async (string id, ProductWriteDto? input, StoreDbContext db) =>
{
    var validation = ProductValidation.Validate(input, creating: false);
    if (validation is not null)
        return BadRequest(validation);
    if (!string.Equals(id, input!.Id, StringComparison.Ordinal))
        return BadRequest("The product id in the URL and body must match");

    await StoreConstants.MutationGate.WaitAsync();
    try
    {
        var product = await db.Products.SingleOrDefaultAsync(candidate => candidate.Id == id);
        if (product is null)
            return Results.NotFound(new { error = "Product not found" });

        if (input.Status is not null && !string.Equals(input.Status, product.Status, StringComparison.Ordinal))
            return BadRequest("status is read-only; use publish or archive to change it");
        if (input.Stock < product.ReservedStock)
            return Results.Conflict(new { error = "stock cannot be lower than reservedStock" });

        input.ApplyTo(product, preserveStatus: true);
        await db.SaveChangesAsync();
        return Results.Ok(ProductDto.FromEntity(product));
    }
    finally
    {
        StoreConstants.MutationGate.Release();
    }
});

app.MapPost("/api/products/{id}/publish", async (string id, StoreDbContext db) =>
{
    await StoreConstants.MutationGate.WaitAsync();
    try
    {
        var product = await db.Products.SingleOrDefaultAsync(candidate => candidate.Id == id);
        if (product is null)
            return Results.NotFound(new { error = "Product not found" });

        product.Status = "published";
        await db.SaveChangesAsync();
        return Results.Ok(ProductDto.FromEntity(product));
    }
    finally
    {
        StoreConstants.MutationGate.Release();
    }
});

app.MapPost("/api/products/{id}/archive", async (string id, StoreDbContext db) =>
{
    await StoreConstants.MutationGate.WaitAsync();
    try
    {
        var product = await db.Products.SingleOrDefaultAsync(candidate => candidate.Id == id);
        if (product is null)
            return Results.NotFound(new { error = "Product not found" });

        product.Status = "archived";
        await db.SaveChangesAsync();
        return Results.Ok(ProductDto.FromEntity(product));
    }
    finally
    {
        StoreConstants.MutationGate.Release();
    }
});

app.MapPost("/api/products/bulk", async (BulkProductRequest? input, StoreDbContext db) =>
{
    var ids = (input?.Ids ?? input?.ProductIds ?? new List<string>())
        .Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToList();
    var action = input?.Action?.Trim().ToLowerInvariant();
    if (ids.Count == 0)
        return BadRequest("At least one product id is required");
    if (action is not ("publish" or "archive" or "category" or "setcategory" or "stock" or "setstock"))
        return BadRequest("action must be publish, archive, category, or stock");

    if (action is "category" or "setcategory")
    {
        var category = input!.Category ?? input.Value;
        if (!ProductValidation.IsCategory(category))
            return BadRequest("category is invalid");
    }

    int? requestedStock = null;
    if (action is "stock" or "setstock")
    {
        requestedStock = input!.Stock;
        if (requestedStock is null && int.TryParse(input.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            requestedStock = parsed;
        if (requestedStock is null || requestedStock < 0)
            return BadRequest("stock must be a nonnegative integer");
    }

    await StoreConstants.MutationGate.WaitAsync();
    try
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        var products = await db.Products.Where(product => ids.Contains(product.Id)).ToListAsync();
        if (products.Count != ids.Count)
            return Results.NotFound(new { error = "One or more products were not found" });
        if (requestedStock is not null && products.Any(product => requestedStock.Value < product.ReservedStock))
            return Results.Conflict(new { error = "stock cannot be lower than an existing reservation" });

        foreach (var product in products)
        {
            switch (action)
            {
                case "publish": product.Status = "published"; break;
                case "archive": product.Status = "archived"; break;
                case "category":
                case "setcategory": product.Category = (input!.Category ?? input.Value)!.Trim().ToLowerInvariant(); break;
                case "stock":
                case "setstock": product.Stock = requestedStock!.Value; break;
            }
        }

        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return Results.Ok(new
        {
            products = products.Select(ProductDto.FromEntity).ToList(),
            updatedProducts = products.Select(ProductDto.FromEntity).ToList()
        });
    }
    finally
    {
        StoreConstants.MutationGate.Release();
    }
});

app.MapGet("/api/orders", async (string? productId, string? status, StoreDbContext db) =>
{
    await StoreConstants.MutationGate.WaitAsync();
    try
    {
        await AbandonStaleCartsAsync(db);
        var query = db.Orders.AsNoTracking().Include(order => order.Items).AsQueryable();
        if (!string.IsNullOrWhiteSpace(productId))
            query = query.Where(order => order.Items.Any(item => item.ProductId == productId));
        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(order => order.Status == status.Trim().ToLowerInvariant());

        var orders = await query.OrderByDescending(order => order.CreatedAt).ToListAsync();
        return Results.Ok(orders.Select(OrderDto.FromEntity).ToList());
    }
    finally
    {
        StoreConstants.MutationGate.Release();
    }
});

app.MapGet("/api/my-orders", async (StoreDbContext db) =>
{
    await StoreConstants.MutationGate.WaitAsync();
    try
    {
        await AbandonStaleCartsAsync(db);
        var orders = await db.Orders.AsNoTracking().Include(order => order.Items)
            .Where(order => order.UserKey == StoreConstants.LocalUser &&
                            order.Status != "open_cart" && order.Status != "abandoned")
            .OrderByDescending(order => order.CreatedAt).ToListAsync();
        return Results.Ok(orders.Select(OrderDto.FromEntity).ToList());
    }
    finally
    {
        StoreConstants.MutationGate.Release();
    }
});

app.MapPost("/api/carts", async (CartRequest? input, StoreDbContext db) =>
{
    var validation = CartValidation.Validate(input);
    if (validation is not null)
        return BadRequest(validation);

    await StoreConstants.MutationGate.WaitAsync();
    try
    {
        var requestedItems = input!.Items!;
        var productIds = requestedItems.Select(item => item.ProductId).Distinct().ToList();
        var products = await db.Products.Where(product => productIds.Contains(product.Id)).ToDictionaryAsync(product => product.Id);
        if (products.Count != productIds.Count)
            return BadRequest("Every cart item must reference an existing product");
        if (products.Values.Any(product => product.Status != "published"))
            return BadRequest("Only published products can be added to a cart");

        var now = DateTime.UtcNow;
        var order = new OrderEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            UserKey = StoreConstants.LocalUser,
            Status = "open_cart",
            CreatedAt = now,
            UpdatedAt = now,
            Items = requestedItems.Select(item => new OrderItemEntity
            {
                ProductId = item.ProductId!,
                ProductName = products[item.ProductId!].Name,
                UnitPrice = products[item.ProductId!].Price,
                Quantity = item.Quantity
            }).ToList()
        };

        db.Orders.Add(order);
        await db.SaveChangesAsync();
        return Results.Created($"/api/orders/{order.Id}", OrderDto.FromEntity(order));
    }
    finally
    {
        StoreConstants.MutationGate.Release();
    }
});

app.MapPost("/api/carts/{id}/submit", async (string id, StoreDbContext db) =>
{
    await StoreConstants.MutationGate.WaitAsync();
    try
    {
        // Sweep first and commit it independently so a failed target operation
        // cannot roll back abandonment updates.
        await AbandonStaleCartsAsync(db);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var order = await db.Orders.Include(candidate => candidate.Items)
            .SingleOrDefaultAsync(candidate => candidate.Id == id);
        if (order is null)
            return Results.NotFound(new { error = "Cart not found" });
        if (order.Status != "open_cart")
            return Results.Conflict(new { error = "Only an open cart can be submitted", order = OrderDto.FromEntity(order) });

        var productIds = order.Items.Select(item => item.ProductId).Distinct().ToList();
        var products = await db.Products.Where(product => productIds.Contains(product.Id)).ToDictionaryAsync(product => product.Id);
        if (products.Count != productIds.Count || products.Values.Any(product => product.Status != "published"))
            return Results.Conflict(new { error = "One or more cart products are no longer available" });
        if (order.Items.Any(item => products[item.ProductId].Stock - products[item.ProductId].ReservedStock < item.Quantity))
            return Results.Conflict(new { error = "Insufficient available stock" });

        foreach (var item in order.Items)
            products[item.ProductId].ReservedStock += item.Quantity;
        order.Status = "submitted";
        order.ReservedAt = DateTime.UtcNow;
        order.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return Results.Ok(OrderDto.FromEntity(order));
    }
    finally
    {
        StoreConstants.MutationGate.Release();
    }
});

app.MapPost("/api/orders/{id}/complete", async (string id, StoreDbContext db) =>
{
    await StoreConstants.MutationGate.WaitAsync();
    try
    {
        await AbandonStaleCartsAsync(db);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var order = await db.Orders.Include(candidate => candidate.Items).SingleOrDefaultAsync(candidate => candidate.Id == id);
        if (order is null)
            return Results.NotFound(new { error = "Order not found" });
        if (order.Status == "completed")
            return Results.Ok(OrderDto.FromEntity(order));
        if (order.Status != "submitted")
            return Results.Conflict(new { error = "Only a submitted order can be completed", order = OrderDto.FromEntity(order) });

        var productIds = order.Items.Select(item => item.ProductId).Distinct().ToList();
        var products = await db.Products.Where(product => productIds.Contains(product.Id)).ToDictionaryAsync(product => product.Id);
        if (products.Count != productIds.Count || order.Items.Any(item =>
                products[item.ProductId].Stock < item.Quantity || products[item.ProductId].ReservedStock < item.Quantity))
            return Results.Conflict(new { error = "The order reservation is no longer valid" });

        foreach (var item in order.Items)
        {
            products[item.ProductId].ReservedStock -= item.Quantity;
            products[item.ProductId].Stock -= item.Quantity;
        }
        order.Status = "completed";
        order.CompletedAt = DateTime.UtcNow;
        order.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return Results.Ok(OrderDto.FromEntity(order));
    }
    finally
    {
        StoreConstants.MutationGate.Release();
    }
});

app.MapPost("/api/orders/{id}/cancel", async (string id, StoreDbContext db) =>
{
    await StoreConstants.MutationGate.WaitAsync();
    try
    {
        await AbandonStaleCartsAsync(db);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var order = await db.Orders.Include(candidate => candidate.Items).SingleOrDefaultAsync(candidate => candidate.Id == id);
        if (order is null)
            return Results.NotFound(new { error = "Order not found" });
        if (order.Status == "cancelled")
            return Results.Ok(OrderDto.FromEntity(order));
        if (order.Status != "submitted")
            return Results.Conflict(new { error = "Only a submitted order can be cancelled", order = OrderDto.FromEntity(order) });

        var productIds = order.Items.Select(item => item.ProductId).Distinct().ToList();
        var products = await db.Products.Where(product => productIds.Contains(product.Id)).ToDictionaryAsync(product => product.Id);
        if (products.Count != productIds.Count || order.Items.Any(item => products[item.ProductId].ReservedStock < item.Quantity))
            return Results.Conflict(new { error = "The order reservation is no longer valid" });
        foreach (var item in order.Items)
            products[item.ProductId].ReservedStock -= item.Quantity;

        order.Status = "cancelled";
        order.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return Results.Ok(OrderDto.FromEntity(order));
    }
    finally
    {
        StoreConstants.MutationGate.Release();
    }
});

app.MapGet("/api/reports/sales", async (string? from, string? to, StoreDbContext db) =>
{
    var report = await SalesQueries.QueryAsync(db, from, to);
    return report.Error is not null ? BadRequest(report.Error) : Results.Ok(report.Value);
});

app.MapPost("/api/agent/turns", async (AgentTurnRequest? input, AgentRuntime agent, StoreDbContext db, HttpContext http) =>
{
    var requestId = input?.RequestId?.Trim();
    if (string.IsNullOrWhiteSpace(requestId) || requestId.Length > 100 || input?.ConversationId?.Length > 100 || string.IsNullOrWhiteSpace(input?.Message) || input.Message.Length > 4000)
        return Results.BadRequest(new { kind = "error", conversationId = input?.ConversationId, message = "requestId and a message of 1–4000 characters are required", code = "invalid_request" });
    var result = await agent.TurnAsync(input!, db, requestId, http.RequestAborted);
    app.Logger.LogInformation("Agent turn {RequestId} completed with kind {Kind}", requestId, result.GetType().GetProperty("kind")?.GetValue(result));
    return Results.Ok(result);
});

app.MapPost("/api/agent/actions/{actionId}/decision", async (string actionId, AgentDecisionRequest? input, AgentRuntime agent, StoreDbContext db, HttpContext http) =>
{
    if (input is null || string.IsNullOrWhiteSpace(input.ConversationId) || string.IsNullOrWhiteSpace(input.RequestId) || input.Decision is not ("confirm" or "reject"))
        return Results.BadRequest(new { kind = "error", conversationId = input?.ConversationId, message = "conversationId, requestId and a valid decision are required", code = "invalid_request" });
    return Results.Ok(await agent.DecideAsync(actionId, input, db, http.RequestAborted));
});

app.Run();

static IResult BadRequest(string message) => Results.BadRequest(new { error = message });

static async Task AbandonStaleCartsAsync(StoreDbContext db)
{
    var cutoff = DateTime.UtcNow.AddDays(-3);
    var stale = await db.Orders.Where(order => order.Status == "open_cart" && order.UpdatedAt < cutoff).ToListAsync();
    if (stale.Count == 0)
        return;
    foreach (var order in stale)
    {
        order.Status = "abandoned";
        order.UpdatedAt = DateTime.UtcNow;
    }
    await db.SaveChangesAsync();
}

sealed class AgentTurnRequest
{
    public string? ConversationId { get; set; }
    public string? RequestId { get; set; }
    public string? Message { get; set; }
}

sealed class AgentDecisionRequest
{
    public string ConversationId { get; set; } = "";
    public string RequestId { get; set; } = "";
    public string Decision { get; set; } = "";
}

sealed class AgentRuntime
{
    private static readonly string[] DraftFields = ["name", "category", "price", "stock", "lowStockThreshold", "description"];
    private static readonly Regex DraftCreationVerb = new(@"\b(?:create|prepare|add|make)\b|(?:создай|создайте|создать|подготовь|подготовьте|подготовить|добавь|добавьте|добавить|сформируй|сформируйте|сформировать|оформи|оформите|оформить)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DraftTarget = new(@"\b(?:product|products|item|items|draft)\b|(?:товар|продукт|черновик)\p{L}*", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NegatedDraftIntent = new(@"\b(?:don't|do not|never)\s+(?:create|prepare|add|make)\b|\bне\s+(?:создавай\p{L}*|создавать|создать|подготовь\p{L}*|подготовить|добавь\p{L}*|добавить|надо|нужно)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DraftCancellation = new(@"\b(?:cancel|abort|stop)\b|\b(?:отмени|отменить|отменяй|прекрати|прервать)\p{L}*|\bне\s+(?:надо|нужно|создавай\p{L}*|создавать|создать)(?:\s+(?:создавать|делать|продолжать))?\b|\b(?:don't|do not)\s+(?:create|continue|make|prepare|add)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly string[] DraftFieldLabels = ["название", "категория", "цена", "остаток", "порог низкого остатка", "описание"];
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(120) };
    private readonly SemaphoreSlim _inferenceGate = new(1, 1);
    private readonly string _baseUrl;
    private readonly Dictionary<string, AgentConversation> _conversations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingProduct> _actions = new(StringComparer.Ordinal);
    private readonly object _sync = new();

    public AgentRuntime(IConfiguration configuration)
    {
        var configured = configuration["Agent:BaseUrl"] ?? "http://127.0.0.1:8080";
        if (!Uri.TryCreate(configured, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp ||
            !IPAddress.TryParse(uri.Host, out var address) || !IPAddress.IsLoopback(address))
            throw new InvalidOperationException("Agent:BaseUrl must be a loopback HTTP URL");
        _baseUrl = uri.ToString().TrimEnd('/');
    }

    public async Task<object> TurnAsync(AgentTurnRequest request, StoreDbContext db, string requestId, CancellationToken cancellationToken)
    {
        var conversationId = string.IsNullOrWhiteSpace(request.ConversationId) ? Guid.NewGuid().ToString("N") : request.ConversationId!;
        AgentConversation conversation;
        lock (_sync)
        {
            PruneState();
            if (!_conversations.TryGetValue(conversationId, out conversation!))
            {
                if (_conversations.Count >= 100)
                {
                    var evictable = _conversations.Where(pair => pair.Value.Gate.CurrentCount > 0).OrderBy(pair => pair.Value.LastTouched).FirstOrDefault();
                    if (!string.IsNullOrEmpty(evictable.Key)) _conversations.Remove(evictable.Key);
                }
                _conversations[conversationId] = conversation = new AgentConversation();
            }
        }
        await conversation.Gate.WaitAsync(cancellationToken);
        try
        {
        conversation.LastTouched = DateTimeOffset.UtcNow;
        if (conversation.RequestResponses.TryGetValue(requestId, out var cached)) return cached;
        TrimHistory(conversation.Messages);
        var hasPartialDraft = conversation.AwaitingDraftFields || conversation.Draft.Values.Any(value => value is not null && !IsBlank(value));
        if (hasPartialDraft && DraftCancellation.IsMatch(request.Message!))
        {
            ClearDraft(conversation);
            const string canceledMessage = "Создание черновика отменено. Запись не создана.";
            conversation.Messages.Add(new JsonObject { ["role"] = "user", ["content"] = request.Message });
            conversation.Messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = canceledMessage });
            var canceled = new { kind = "answer", conversationId, message = canceledMessage };
            RememberResponse(conversation, requestId, canceled);
            return canceled;
        }
        var forceDraftTool = conversation.AwaitingDraftFields || IsExplicitDraftIntent(request.Message!);
        if (forceDraftTool) conversation.AwaitingDraftFields = true;
        conversation.Messages.Add(new JsonObject { ["role"] = "user", ["content"] = request.Message });
        var readCalls = 0;
        var turnResults = new List<object>();
        try
        {
            for (var completion = 0; completion < 3; completion++)
            {
                // A live run showed that 512 tokens can truncate even a short final summary.
                // Keep the same bounded budget for tool selection and the follow-up answer.
                var response = await CompleteAsync(conversation.Messages, 1024, forceDraftTool, cancellationToken);
                if (response.Error is not null) return Error(conversationId, response.Error, response.Code!);
                if (response.Payload is null) return forceDraftTool ? DraftClarification(conversation, conversationId, requestId) : Error(conversationId, "The local model returned an invalid response", "model_invalid");
                var choice = response.Payload!["choices"]?.AsArray().FirstOrDefault();
                if (choice is null) return forceDraftTool ? DraftClarification(conversation, conversationId, requestId) : Error(conversationId, "The local model returned an invalid response", "model_invalid");
                if (choice["finish_reason"]?.GetValue<string>() == "length") return forceDraftTool ? DraftClarification(conversation, conversationId, requestId) : Error(conversationId, "The local model response was incomplete; please try again", "model_incomplete");
                var assistant = choice["message"] as JsonObject;
                if (assistant is null) return forceDraftTool ? DraftClarification(conversation, conversationId, requestId) : Error(conversationId, "The local model returned an invalid response", "model_invalid");
                var calls = assistant["tool_calls"] as JsonArray;
                if (calls is null || calls.Count == 0)
                {
                    if (forceDraftTool) return DraftClarification(conversation, conversationId, requestId);
                    var text = assistant["content"]?.GetValue<string>() ?? "I could not produce an answer.";
                    conversation.Messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = text });
                    var answer = turnResults.Count == 0
                        ? new { kind = "answer", conversationId, message = text, results = (object?)null }
                        : new { kind = "answer", conversationId, message = text, results = (object?)turnResults };
                    RememberResponse(conversation, requestId, answer);
                    return answer;
                }
                assistant.Remove("reasoning_content");
                conversation.Messages.Add((JsonObject)assistant.DeepClone());
                for (var callIndex = 0; callIndex < calls.Count; callIndex++)
                {
                    var call = calls[callIndex];
                    var name = call?["function"]?["name"]?.GetValue<string>();
                    var callId = call?["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N");
                    if (forceDraftTool && name != "create_product_draft")
                        return DraftToolClarification(conversation, conversationId, requestId, calls, callIndex);
                    if (!TryArguments(call, out var args, out var error))
                        return forceDraftTool ? DraftToolClarification(conversation, conversationId, requestId, calls, callIndex) : Error(conversationId, error, "invalid_tool_call");
                    if (name == "create_product_draft")
                    {
                        foreach (var pair in args!) conversation.Draft[pair.Key] = pair.Value?.DeepClone();
                        var draftCategory = conversation.Draft["category"]?.GetValue<string>();
                        if (!ProductValidation.IsCategory(draftCategory)) conversation.Draft["category"] = null;
                        var missing = DraftFields.Where(field => conversation.Draft[field] is null || IsBlank(conversation.Draft[field])).ToArray();
                        if (missing.Length > 0)
                        {
                            var clarificationResult = BuildDraftClarification(conversation, conversationId, requestId, missing);
                            conversation.Messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = callId, ["content"] = JsonSerializer.Serialize(clarificationResult) });
                            AppendSkippedToolCalls(conversation.Messages, calls, callIndex + 1);
                            RememberResponse(conversation, requestId, clarificationResult);
                            return clarificationResult;
                        }
                        var dto = BuildDraft(conversation.Draft);
                        dto.Id = await UniqueSlugAsync(db, dto.Name!);
                        dto.Status = "draft"; dto.Accent = "#a3e635"; dto.Mark = "NEW"; dto.Tags = [];
                        var validation = ProductValidation.Validate(dto, creating: true);
                        if (validation is not null) return Error(conversationId, validation, "invalid_product");
                        var snapshot = ProductDto.FromEntity(dto.ToEntity());
                        var actionId = Guid.NewGuid().ToString("N");
                        lock (_sync)
                        {
                            PruneState();
                            if (_actions.Count >= 100) _actions.Remove(_actions.OrderBy(pair => pair.Value.ExpiresAt).First().Key);
                            _actions[actionId] = new PendingProduct(conversationId, snapshot, DateTimeOffset.UtcNow.AddMinutes(5), callId);
                        }
                        var confirmationResult = new { kind = "confirmation_required", conversationId, message = "Review this product draft and confirm or reject it.", actionId, preview = snapshot };
                        ClearDraft(conversation);
                        ClearDraft(conversation);
                        conversation.Messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = callId, ["content"] = JsonSerializer.Serialize(new { kind = confirmationResult.kind, actionId, preview = snapshot }) });
                        AppendSkippedToolCalls(conversation.Messages, calls, callIndex + 1);
                        RememberResponse(conversation, requestId, confirmationResult);
                        return confirmationResult;
                    }
                    if (name != "list_products" && name != "get_sales_report") return Error(conversationId, "The requested tool is not available", "unknown_tool");
                    if (++readCalls > 2) return Error(conversationId, "The local agent reached its read limit", "tool_limit");
                    var result = await ExecuteReadToolAsync(name!, args!, db);
                    turnResults.Add(result);
                    conversation.Messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = callId, ["content"] = JsonSerializer.Serialize(result) });
                }
                if (readCalls > 0)
                {
                    var message = string.Join("\n", turnResults.Select(BuildReadSummary));
                    conversation.Messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = message });
                    var answer = new { kind = "answer", conversationId, message, results = turnResults };
                    RememberResponse(conversation, requestId, answer);
                    return answer;
                }
            }
            return Error(conversationId, "The local agent reached its completion limit", "turn_limit");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return Error(conversationId, "The local model timed out", "model_timeout"); }
        catch (HttpRequestException) { return Error(conversationId, "The local model is unavailable", "model_unavailable"); }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or ArgumentException)
        { return Error(conversationId, "The local model response could not be processed", "model_invalid"); }
        }
        finally
        {
            conversation.LastTouched = DateTimeOffset.UtcNow;
            conversation.Gate.Release();
        }
    }

    public async Task<object> DecideAsync(string actionId, AgentDecisionRequest request, StoreDbContext db, CancellationToken cancellationToken)
    {
        PendingProduct? action;
        lock (_sync) _actions.TryGetValue(actionId, out action);
        if (action is null || action.ConversationId != request.ConversationId) return Error(request.ConversationId, "Pending action not found", "action_not_found");
        await StoreConstants.MutationGate.WaitAsync(cancellationToken);
        try
        {
            if (action.ExpiresAt <= DateTimeOffset.UtcNow) { lock (_sync) _actions.Remove(actionId); return Error(request.ConversationId, "Pending action expired", "action_expired"); }
            if (action.Completed is not null) return action.Completed;
            if (request.Decision == "reject")
            {
                action.Completed = new { kind = "answer", conversationId = request.ConversationId, message = "The product draft was rejected." };
                await AppendActionOutcomeAsync(action, action.Completed, cancellationToken);
                return action.Completed;
            }
            var product = action.Product;
            var write = new ProductWriteDto { Id = product.Id, Name = product.Name, Category = product.Category, Price = product.Price, Stock = product.Stock,
                LowStockThreshold = product.LowStockThreshold, Status = "draft", Accent = product.Accent, Mark = product.Mark, Description = product.Description, Tags = product.Tags };
            var validation = ProductValidation.Validate(write, creating: true);
            if (validation is not null) return Error(request.ConversationId, validation, "invalid_product");
            if (await db.Products.AnyAsync(existing => existing.Id == product.Id, cancellationToken))
                return Error(request.ConversationId, "The product ID was taken after preview. Start a new draft to refresh the preview.", "product_id_conflict");
            var entity = write.ToEntity();
            db.Products.Add(entity);
            await db.SaveChangesAsync(cancellationToken);
            var summary = new { id = entity.Id, name = entity.Name, category = entity.Category, price = entity.Price, availableStock = entity.Stock - entity.ReservedStock, status = entity.Status };
            action.Completed = new { kind = "answer", conversationId = request.ConversationId, message = "Product draft created.", results = new[] { new { type = "products", items = new[] { summary }, totalMatches = 1, hasMore = false } } };
            await AppendActionOutcomeAsync(action, action.Completed, cancellationToken);
            return action.Completed;
        }
        finally { StoreConstants.MutationGate.Release(); }
    }

    private async Task<(JsonObject? Payload, string? Error, string? Code)> CompleteAsync(JsonArray messages, int maxTokens, bool forceDraftTool, CancellationToken cancellationToken)
    {
        var draftTool = Tool("create_product_draft", "Prepare a product draft. Provide only business fields explicitly supplied by the user; do not guess missing details. Missing or unsupported categories will be clarified. Category must be one of the exact supported values.", new JsonObject { ["name"] = StringProp(), ["category"] = CategoryProp("Exact product category: audio, computers, wearables, or workspace. Do not infer an unsupported category."), ["price"] = new JsonObject { ["type"] = "integer" }, ["stock"] = new JsonObject { ["type"] = "integer" }, ["lowStockThreshold"] = new JsonObject { ["type"] = "integer" }, ["description"] = StringProp() }, []);
        var tools = new JsonArray(
            Tool("list_products", "Search the product catalog. Category must be one of the exact supported values.", new JsonObject { ["status"] = StringProp(), ["category"] = CategoryProp("Exact product category: audio, computers, wearables, or workspace."), ["query"] = StringProp(), ["maxPrice"] = new JsonObject { ["type"] = "integer" }, ["lowStockOnly"] = new JsonObject { ["type"] = "boolean" } }, []),
            Tool("get_sales_report", "Read completed sales for an optional date range", new JsonObject { ["from"] = StringProp(), ["to"] = StringProp() }, []),
            draftTool);
        // llama.cpp's OpenAI-compatible parser accepts a string tool_choice, not the
        // named-function object variant. With only the draft tool present, "required"
        // forces a structured call without exposing the read tools on a write turn.
        JsonNode? toolChoice = JsonValue.Create(forceDraftTool ? "required" : "auto");
        var body = new JsonObject { ["model"] = "local", ["messages"] = messages.DeepClone(), ["tools"] = forceDraftTool ? new JsonArray(draftTool.DeepClone()) : tools, ["tool_choice"] = toolChoice, ["max_tokens"] = maxTokens,
            ["temperature"] = 0.6, ["top_p"] = 0.95, ["top_k"] = 20 };
        await _inferenceGate.WaitAsync(cancellationToken);
        try
        {
            using var response = await _http.PostAsJsonAsync(_baseUrl + "/v1/chat/completions", body, cancellationToken);
            if (!response.IsSuccessStatusCode) return (null, "The local model is unavailable", "model_unavailable");
            try { return (await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken), null, null); }
            catch (JsonException) { return (null, "The local model returned an invalid response", "model_invalid"); }
        }
        finally { _inferenceGate.Release(); }
    }

    private static JsonObject Tool(string name, string description, JsonObject properties, string[] required) => new()
    { ["type"] = "function", ["function"] = new JsonObject { ["name"] = name, ["description"] = description, ["parameters"] = new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = new JsonArray(required.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()), ["additionalProperties"] = false } } };
    private static JsonObject StringProp() => new() { ["type"] = "string" };
    private static JsonObject CategoryProp(string description) => new()
    {
        ["type"] = "string", ["description"] = description,
        ["enum"] = new JsonArray("audio", "computers", "wearables", "workspace")
    };

    private static bool TryArguments(JsonNode? call, out JsonObject? args, out string error)
    {
        args = null; error = "Tool arguments must be valid JSON";
        var raw = call?["function"]?["arguments"];
        try { args = raw is JsonObject obj ? (JsonObject)obj.DeepClone() : JsonNode.Parse(raw?.GetValue<string>() ?? "") as JsonObject; }
        catch (JsonException) { return false; }
        if (args is null) return false;
        var name = call?["function"]?["name"]?.GetValue<string>();
        var allowed = name switch { "list_products" => new[] { "status", "category", "query", "maxPrice", "lowStockOnly" }, "get_sales_report" => new[] { "from", "to" }, "create_product_draft" => DraftFields, _ => [] };
        if (args.Any(pair => !allowed.Contains(pair.Key, StringComparer.Ordinal))) { error = "Tool arguments contain unsupported properties"; return false; }
        var stringFields = name switch { "list_products" => new[] { "status", "category", "query" }, "get_sales_report" => new[] { "from", "to" }, "create_product_draft" => new[] { "name", "category", "description" }, _ => [] };
        foreach (var field in stringFields)
            if (args[field] is JsonNode node && (node is not JsonValue value || !value.TryGetValue<string>(out _)))
            { error = "Tool arguments contain values of the wrong type"; return false; }
        if (name == "list_products" && args["query"] is JsonValue queryValue && queryValue.TryGetValue<string>(out var queryText) && queryText.Length > 100)
        { error = "query is too long"; return false; }
        if (name == "list_products")
        {
            if (args["maxPrice"] is JsonValue price && (!price.TryGetValue<int>(out var amount) || amount < 0 || amount > 10_000_000)) { error = "maxPrice must be between 0 and 10000000"; return false; }
            if (args["lowStockOnly"] is JsonValue lowStock && !lowStock.TryGetValue<bool>(out _)) { error = "lowStockOnly must be a boolean"; return false; }
            if (args["category"] is JsonValue category && category.TryGetValue<string>(out var categoryText) && !ProductValidation.IsCategory(categoryText)) { error = "category is invalid"; return false; }
            if (args["status"] is JsonValue status && status.TryGetValue<string>(out var statusText) && statusText is not ("all" or "draft" or "published" or "archived")) { error = "status is invalid"; return false; }
        }
        if (name == "get_sales_report")
            foreach (var field in new[] { "from", "to" })
                if (args[field] is JsonValue date && (!date.TryGetValue<string>(out var dateText) || !IsIsoDate(dateText)))
                { error = "from and to must be ISO dates"; return false; }
        if (name == "create_product_draft")
        {
            foreach (var field in new[] { "price", "stock", "lowStockThreshold" })
                if (args[field] is JsonValue numeric && !numeric.TryGetValue<int>(out _)) { error = $"{field} must be an integer"; return false; }
        }
        return true;
    }

    private static bool IsIsoDate(string? value) => value is not null && Regex.IsMatch(value, @"^\d{4}-\d{2}-\d{2}(?:T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:?\d{2})?)?$") && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _);

    private static async Task<object> ExecuteReadToolAsync(string name, JsonObject args, StoreDbContext db)
    {
        if (name == "get_sales_report")
        {
            var report = await SalesQueries.QueryAsync(db, args["from"]?.GetValue<string>(), args["to"]?.GetValue<string>());
            if (report.Error is not null) throw new ArgumentException(report.Error);
            var serialized = JsonSerializer.SerializeToNode(report.Value)!.AsObject();
            var allProducts = serialized["products"]!.AsArray();
            var productRows = allProducts.Select(row => new
                {
                    productId = row!["productId"]!.GetValue<string>(),
                    name = row["name"]!.GetValue<string>(),
                    quantity = row["quantity"]!.GetValue<long>(),
                    revenue = row["revenue"]!.GetValue<long>()
                })
                .OrderByDescending(product => product.revenue).ThenBy(product => product.name, StringComparer.Ordinal).Take(10).ToList();
            return new
            {
                type = "salesReport",
                report = new
                {
                    from = serialized["from"], to = serialized["to"], completedOrders = serialized["completedOrders"], revenue = serialized["revenue"],
                    products = productRows, totalProducts = allProducts.Count, hasMore = allProducts.Count > productRows.Count
                }
            };
        }
        var query = db.Products.AsNoTracking().AsQueryable();
        var status = args["status"]?.GetValue<string>(); if (!string.IsNullOrWhiteSpace(status) && status != "all") query = query.Where(p => p.Status == status);
        var category = args["category"]?.GetValue<string>(); if (!string.IsNullOrWhiteSpace(category)) query = query.Where(p => p.Category == category);
        var term = args["query"]?.GetValue<string>(); if (!string.IsNullOrWhiteSpace(term)) query = query.Where(p => p.Name.Contains(term) || p.Id.Contains(term) || p.TagsJson.Contains(term));
        if (args["maxPrice"] is JsonValue max && max.TryGetValue<int>(out var maxPrice)) query = query.Where(p => p.Price <= maxPrice);
        if (args["lowStockOnly"]?.GetValue<bool>() == true) query = query.Where(p => p.Stock - p.ReservedStock <= p.LowStockThreshold);
        var totalMatches = await query.CountAsync();
        var rows = await query.OrderBy(p => p.Name).Take(9).Select(p => new { p.Id, p.Name, p.Category, p.Price, AvailableStock = p.Stock - p.ReservedStock, p.Status }).ToListAsync();
        return new { type = "products", items = rows.Take(8).ToList(), totalMatches, hasMore = totalMatches > 8 };
    }

    private static string BuildReadSummary(object result)
    {
        var node = JsonSerializer.SerializeToNode(result)!.AsObject();
        var type = node["type"]?.GetValue<string>();
        if (type == "products")
        {
            var items = node["items"]?.AsArray().Count ?? 0;
            var total = node["totalMatches"]?.GetValue<int>() ?? 0;
            return $"Каталог: найдено товаров — {total}, показано — {items}.";
        }
        if (type == "salesReport")
        {
            var report = node["report"]!.AsObject();
            var completedOrders = report["completedOrders"]!.GetValue<int>();
            var revenue = report["revenue"]!.GetValue<long>();
            var from = report["from"]?.GetValue<DateTime>();
            var to = report["to"]?.GetValue<DateTime>();
            var period = from?.Year == 1 && to?.Year == 9999
                ? "за весь доступный период"
                : $"за период {from:yyyy-MM-dd} — {to:yyyy-MM-dd}";
            var formattedRevenue = revenue.ToString("N0", CultureInfo.InvariantCulture).Replace(',', ' ');
            return $"Отчёт по продажам {period}: завершённых заказов — {completedOrders}, выручка — {formattedRevenue} ₽.";
        }
        return "Запрос выполнен.";
    }

    private static bool IsExplicitDraftIntent(string message) =>
        !NegatedDraftIntent.IsMatch(message) && !DraftCancellation.IsMatch(message) && DraftCreationVerb.IsMatch(message) && DraftTarget.IsMatch(message);

    private static object DraftClarification(AgentConversation conversation, string conversationId, string requestId)
    {
        var missing = GetMissingDraftFields(conversation);
        if (missing.Length == 0)
            return Error(conversationId, "Не удалось безопасно разобрать параметры черновика. Повторите недостающие данные.", "invalid_draft_arguments");
        var result = BuildDraftClarification(conversation, conversationId, requestId, missing);
        conversation.Messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = result.GetType().GetProperty("message")?.GetValue(result)?.ToString() });
        return result;
    }

    private static object DraftToolClarification(AgentConversation conversation, string conversationId, string requestId, JsonArray calls, int callIndex)
    {
        var missing = GetMissingDraftFields(conversation);
        if (missing.Length == 0)
            return Error(conversationId, "Не удалось безопасно разобрать параметры черновика. Повторите недостающие данные.", "invalid_draft_arguments");
        var result = BuildDraftClarification(conversation, conversationId, requestId, missing);
        var callId = calls[callIndex]?["id"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(callId))
            conversation.Messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = callId, ["content"] = JsonSerializer.Serialize(result) });
        AppendSkippedToolCalls(conversation.Messages, calls, callIndex + 1);
        return result;
    }

    private static object BuildDraftClarification(AgentConversation conversation, string conversationId, string requestId, string[] missing)
    {
        conversation.AwaitingDraftFields = true;
        var labels = missing.Select(field => DraftFieldLabels[Array.IndexOf(DraftFields, field)]);
        var result = new { kind = "clarification", conversationId, message = "Для черновика уточните: " + string.Join(", ", labels) + ".", missingFields = missing };
        RememberResponse(conversation, requestId, result);
        return result;
    }

    private static string[] GetMissingDraftFields(AgentConversation conversation) => DraftFields
        .Where(field => conversation.Draft[field] is null || IsBlank(conversation.Draft[field])).ToArray();

    private static void ClearDraft(AgentConversation conversation)
    {
        foreach (var field in DraftFields) conversation.Draft[field] = null;
        conversation.AwaitingDraftFields = false;
    }

    private static ProductWriteDto BuildDraft(Dictionary<string, JsonNode?> draft) => new()
    {
        Name = draft["name"]?.GetValue<string>()?.Trim(), Category = draft["category"]?.GetValue<string>()?.Trim().ToLowerInvariant(),
        Price = draft["price"]?.GetValue<int>(), Stock = draft["stock"]?.GetValue<int>(), LowStockThreshold = draft["lowStockThreshold"]?.GetValue<int>(),
        Description = draft["description"]?.GetValue<string>()?.Trim(), Accent = "#a3e635", Mark = "NEW", Tags = [], Status = "draft"
    };
    private static bool IsBlank(JsonNode? value) => value is null || value.ToJsonString() == "null" || (value is JsonValue v && v.TryGetValue<string>(out var s) && string.IsNullOrWhiteSpace(s));
    private static async Task<string> UniqueSlugAsync(StoreDbContext db, string name)
    {
        var slug = Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (slug.Length < 2) slug = "product-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..10].ToLowerInvariant();
        slug = slug[..Math.Min(64, slug.Length)]; var candidate = slug; var suffix = 2;
        while (await db.Products.AnyAsync(p => p.Id == candidate)) { var ending = "-" + suffix++; candidate = slug[..Math.Min(slug.Length, 64 - ending.Length)] + ending; }
        return candidate;
    }
    private void PruneState()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var key in _conversations.Where(pair => pair.Value.Gate.CurrentCount > 0 && now - pair.Value.LastTouched > TimeSpan.FromMinutes(30)).Select(pair => pair.Key).ToArray())
            _conversations.Remove(key);
        foreach (var key in _actions.Where(pair => pair.Value.ExpiresAt <= now).Select(pair => pair.Key).ToArray())
            _actions.Remove(key);
    }
    private static void TrimHistory(JsonArray messages)
    {
        while (messages.Count > 24 || messages.Sum(node => node?["content"]?.ToJsonString().Length ?? 0) > 4000)
        {
            var nextUser = -1;
            for (var i = 2; i < messages.Count; i++)
                if (messages[i]?["role"]?.GetValue<string>() == "user") { nextUser = i; break; }
            var removeCount = nextUser < 0 ? messages.Count - 1 : nextUser - 1;
            if (removeCount <= 0) break;
            for (var i = 0; i < removeCount; i++) messages.RemoveAt(1);
        }
    }
    private async Task AppendActionOutcomeAsync(PendingProduct action, object outcome, CancellationToken cancellationToken)
    {
        AgentConversation? conversation;
        lock (_sync)
        {
            if (!_conversations.TryGetValue(action.ConversationId, out conversation)) return;
        }
        await conversation.Gate.WaitAsync(cancellationToken);
        try
        {
            conversation.Messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = outcome.GetType().GetProperty("message")?.GetValue(outcome)?.ToString() ?? "The product action is complete." });
            conversation.LastTouched = DateTimeOffset.UtcNow;
        }
        finally { conversation.Gate.Release(); }
    }
    private static void RememberResponse(AgentConversation conversation, string requestId, object response)
    {
        conversation.RequestResponses[requestId] = response;
        while (conversation.RequestResponses.Count > 32)
            conversation.RequestResponses.Remove(conversation.RequestResponses.Keys.First());
    }
    private static void AppendSkippedToolCalls(JsonArray messages, JsonArray calls, int fromIndex)
    {
        for (var i = fromIndex; i < calls.Count; i++)
        {
            var id = calls[i]?["id"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(id))
                messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = id, ["content"] = "Not executed because the action requires user input or confirmation." });
        }
    }
    private static object Error(string conversationId, string message, string code) => new { kind = "error", conversationId, message, code };
    private sealed class AgentConversation
    {
        public JsonArray Messages { get; } = [new JsonObject { ["role"] = "system", ["content"] = "You are a local Signal Store CRM assistant. Use only the provided tools for catalog and sales facts. Reply concisely in Russian. Never expose reasoning, hidden notes, XML/HTML tags, or internal narration in visible answers. For an explicit product-draft request, use create_product_draft and send only values known from the user; never invent missing required business fields. Ask for missing values. The only valid product categories are exactly audio, computers, wearables, and workspace." }];
        public Dictionary<string, JsonNode?> Draft { get; } = DraftFields.ToDictionary(field => field, _ => (JsonNode?)null);
        public bool AwaitingDraftFields { get; set; }
        public Dictionary<string, object> RequestResponses { get; } = new(StringComparer.Ordinal);
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public DateTimeOffset LastTouched { get; set; } = DateTimeOffset.UtcNow;
    }
    private sealed class PendingProduct(string conversationId, ProductDto product, DateTimeOffset expiresAt, string toolCallId)
    { public string ConversationId { get; } = conversationId; public ProductDto Product { get; } = product; public DateTimeOffset ExpiresAt { get; } = expiresAt; public string ToolCallId { get; } = toolCallId; public object? Completed { get; set; } }
}

static class SalesQueries
{
    public static async Task<(object? Value, string? Error)> QueryAsync(StoreDbContext db, string? from, string? to)
    {
        if (!ParseDate(from, DateTime.MinValue, false, out var fromDate) || !ParseDate(to, DateTime.MaxValue, true, out var toDate))
            return (null, "from and to must be valid dates");
        if (fromDate > toDate) return (null, "from must not be later than to");
        var orders = await db.Orders.AsNoTracking().Include(order => order.Items)
            .Where(order => order.Status == "completed" && order.CompletedAt != null && order.CompletedAt >= fromDate && order.CompletedAt <= toDate)
            .OrderByDescending(order => order.CompletedAt).ToListAsync();
        var products = orders.SelectMany(order => order.Items).GroupBy(item => item.ProductId)
            .Select(group => new { productId = group.Key, name = group.OrderBy(item => item.ProductName).Select(item => item.ProductName).FirstOrDefault() ?? group.Key,
                quantity = group.Sum(item => (long)item.Quantity), revenue = group.Sum(item => (long)item.UnitPrice * item.Quantity), units = group.Sum(item => (long)item.Quantity) })
            .OrderBy(item => item.name).ToList();
        var revenue = products.Sum(item => item.revenue);
        return (new { from = fromDate, to = toDate, completedOrders = orders.Count, orderCount = orders.Count, revenue, totalRevenue = revenue, products, items = products }, null);
    }

    private static bool ParseDate(string? value, DateTime fallback, bool inclusiveEnd, out DateTime result)
    {
        if (string.IsNullOrWhiteSpace(value)) { result = fallback; return true; }
        if (!DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out result)) return false;
        if (inclusiveEnd && !value.Contains(':') && !value.Contains('T', StringComparison.OrdinalIgnoreCase)) result = result.Date.AddDays(1).AddTicks(-1);
        return true;
    }
}

sealed class StoreDbContext(DbContextOptions<StoreDbContext> options) : DbContext(options)
{
    public DbSet<ProductEntity> Products => Set<ProductEntity>();
    public DbSet<OrderEntity> Orders => Set<OrderEntity>();
    public DbSet<OrderItemEntity> OrderItems => Set<OrderItemEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProductEntity>().HasKey(product => product.Id);
        modelBuilder.Entity<OrderEntity>().HasKey(order => order.Id);
        modelBuilder.Entity<OrderItemEntity>().HasKey(item => item.Id);
        modelBuilder.Entity<OrderEntity>().HasMany(order => order.Items).WithOne().HasForeignKey(item => item.OrderId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<OrderItemEntity>().HasOne<ProductEntity>().WithMany().HasForeignKey(item => item.ProductId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<ProductEntity>().Property(product => product.TagsJson).IsRequired();
        modelBuilder.Entity<OrderItemEntity>().HasIndex(item => item.ProductId);
        modelBuilder.Entity<ProductEntity>().ToTable("Products", table =>
        {
            table.HasCheckConstraint("CK_Products_Price", "Price >= 1");
            table.HasCheckConstraint("CK_Products_Stock", "Stock >= 0");
            table.HasCheckConstraint("CK_Products_ReservedStock", "ReservedStock >= 0 AND ReservedStock <= Stock");
            table.HasCheckConstraint("CK_Products_LowStockThreshold", "LowStockThreshold >= 0");
            table.HasCheckConstraint("CK_Products_Category", "Category IN ('audio', 'computers', 'wearables', 'workspace')");
            table.HasCheckConstraint("CK_Products_Status", "Status IN ('draft', 'published', 'archived')");
        });
        modelBuilder.Entity<OrderEntity>().ToTable("Orders", table =>
            table.HasCheckConstraint("CK_Orders_Status", "Status IN ('open_cart', 'abandoned', 'submitted', 'completed', 'cancelled')"));
        modelBuilder.Entity<OrderItemEntity>().ToTable("OrderItems", table =>
        {
            table.HasCheckConstraint("CK_OrderItems_Quantity", "Quantity > 0");
            table.HasCheckConstraint("CK_OrderItems_UnitPrice", "UnitPrice >= 0");
        });
    }
}

sealed class ProductEntity
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public int Price { get; set; }
    public int Stock { get; set; }
    public int ReservedStock { get; set; }
    public int LowStockThreshold { get; set; }
    public string Status { get; set; } = "draft";
    public string Accent { get; set; } = "#000000";
    public string Mark { get; set; } = "";
    public string Description { get; set; } = "";
    public string TagsJson { get; set; } = "[]";
}

sealed class OrderEntity
{
    public string Id { get; set; } = "";
    public string UserKey { get; set; } = StoreConstants.LocalUser;
    public string Status { get; set; } = "open_cart";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? ReservedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public List<OrderItemEntity> Items { get; set; } = [];
}

sealed class OrderItemEntity
{
    public int Id { get; set; }
    public string OrderId { get; set; } = "";
    public string ProductId { get; set; } = "";
    public string ProductName { get; set; } = "";
    public int UnitPrice { get; set; }
    public int Quantity { get; set; }
}

sealed class ProductWriteDto
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Category { get; set; }
    public int? Price { get; set; }
    public int? Stock { get; set; }
    public int? LowStockThreshold { get; set; }
    public string? Status { get; set; }
    public string? Accent { get; set; }
    public string? Mark { get; set; }
    public string? Description { get; set; }
    public List<string>? Tags { get; set; }

    public ProductEntity ToEntity()
    {
        var product = new ProductEntity();
        ApplyTo(product, preserveStatus: false);
        product.Status = "draft";
        return product;
    }

    public void ApplyTo(ProductEntity product, bool preserveStatus)
    {
        product.Id = Id!;
        product.Name = Name!;
        product.Category = Category!.Trim().ToLowerInvariant();
        product.Price = Price!.Value;
        product.Stock = Stock!.Value;
        product.LowStockThreshold = LowStockThreshold!.Value;
        // Product status is read-only here. Only the publish/archive commands may change it.
        if (!preserveStatus && !string.IsNullOrWhiteSpace(Status))
            product.Status = Status!.Trim().ToLowerInvariant();
        product.Accent = Accent!;
        product.Mark = Mark!;
        product.Description = Description!;
        product.TagsJson = JsonSerializer.Serialize(Tags ?? []);
    }
}

sealed class BulkProductRequest
{
    public List<string>? Ids { get; set; }
    public List<string>? ProductIds { get; set; }
    public string? Action { get; set; }
    public string? Category { get; set; }
    public int? Stock { get; set; }
    public string? Value { get; set; }
}

sealed class CartRequest
{
    public List<CartItemRequest>? Items { get; set; }
}

sealed class CartItemRequest
{
    public string? ProductId { get; set; }
    public int Quantity { get; set; }
}

static class ProductValidation
{
    private static readonly Regex IdPattern = new("^[a-z0-9][a-z0-9-]{1,63}$", RegexOptions.Compiled);
    private static readonly Regex HexPattern = new("^#[0-9a-fA-F]{6}$", RegexOptions.Compiled);

    public static string? Validate(ProductWriteDto? input, bool creating)
    {
        if (input is null) return "A product body is required";
        if (string.IsNullOrWhiteSpace(input.Id) || !IdPattern.IsMatch(input.Id)) return "id must be a lowercase slug";
        if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > 120) return "name must be 1 to 120 characters";
        if (!IsCategory(input.Category)) return "category is invalid";
        if (input.Price is null or < 1 or > 10_000_000) return "price must be between 1 and 10000000";
        if (input.Stock is null or < 0 or > 1_000_000) return "stock must be a nonnegative integer";
        if (input.LowStockThreshold is null or < 0 or > 1_000_000) return "lowStockThreshold must be nonnegative";
        if (string.IsNullOrWhiteSpace(input.Accent) || !HexPattern.IsMatch(input.Accent)) return "accent must be a six-digit HEX color";
        if (string.IsNullOrWhiteSpace(input.Mark) || input.Mark.Length > 16) return "mark must be 1 to 16 characters";
        if (string.IsNullOrWhiteSpace(input.Description) || input.Description.Length > 1000) return "description must be 1 to 1000 characters";
        if (input.Tags is null || input.Tags.Count > 30 || input.Tags.Any(tag => string.IsNullOrWhiteSpace(tag) || tag.Length > 80)) return "tags are invalid";
        if (input.Status is not null && !StoreConstants.ProductStatuses.Contains(input.Status)) return "status is invalid";
        if (creating && input.Status is not null && input.Status != "draft") return "new products may only be created as draft";
        return null;
    }

    public static bool IsCategory(string? category) => category is not null && StoreConstants.AllowedCategories.Contains(category.Trim().ToLowerInvariant());
}

static class CartValidation
{
    public static string? Validate(CartRequest? input)
    {
        if (input?.Items is not { Count: > 0 }) return "items must contain at least one product";
        if (input.Items.Any(item => string.IsNullOrWhiteSpace(item.ProductId) || item.Quantity < 1 || item.Quantity > 1_000)) return "each item needs a positive quantity";
        if (input.Items.Select(item => item.ProductId).Distinct(StringComparer.Ordinal).Count() != input.Items.Count) return "a product may occur only once in a cart";
        return null;
    }
}

sealed record ProductDto(
    string Id, string Name, string Category, int Price, int Stock, int ReservedStock,
    int AvailableStock, int LowStockThreshold, string Status, string Accent, string Mark,
    string Description, List<string> Tags)
{
    public static ProductDto FromEntity(ProductEntity product) => new(
        product.Id, product.Name, product.Category, product.Price, product.Stock,
        product.ReservedStock, product.Stock - product.ReservedStock, product.LowStockThreshold,
        product.Status, product.Accent, product.Mark, product.Description,
        JsonSerializer.Deserialize<List<string>>(product.TagsJson) ?? []);
}

sealed record OrderItemDto(string ProductId, string Name, string ProductName, int Price, int UnitPrice, int Quantity, long LineTotal)
{
    public static OrderItemDto FromEntity(OrderItemEntity item) => new(item.ProductId, item.ProductName, item.ProductName,
        item.UnitPrice, item.UnitPrice, item.Quantity, (long)item.UnitPrice * item.Quantity);
}

sealed record OrderDto(
    string Id, string Status, DateTime CreatedAt, DateTime UpdatedAt,
    List<OrderItemDto> Items, DateTime? ReservedAt, DateTime? CompletedAt, long Total, long TotalAmount)
{
    public static OrderDto FromEntity(OrderEntity order)
    {
        var items = order.Items.OrderBy(item => item.Id).Select(OrderItemDto.FromEntity).ToList();
        var total = items.Sum(item => item.LineTotal);
        return new(order.Id, order.Status, order.CreatedAt, order.UpdatedAt, items,
            order.ReservedAt, order.CompletedAt, total, total);
    }
}

static class SeedData
{
    public static async Task InitializeAsync(StoreDbContext db)
    {
        if (await db.Products.AnyAsync()) return;

        var products = new List<ProductEntity>();
        var firstProducts = new (string id, string name, string category, int price, string accent, string mark, string description, string[] tags)[]
        {
            ("orbit-buds", "Orbit Buds S", "audio", 6990, "#ff6b35", "OB", "Компактные TWS-наушники с шумоподавлением и 28 часами работы.", ["Bluetooth 5.4", "ANC", "28 часов"]),
            ("vector-air", "VectorBook Air 14", "computers", 89990, "#a3e635", "VA", "Лёгкий ноутбук с OLED-экраном, 16 Гб памяти и тихим характером.", ["OLED 2.8K", "16 / 512 Гб", "1.18 кг"]),
            ("pulse-watch", "Pulse Watch 2", "wearables", 14990, "#38bdf8", "PW", "Часы для спорта, сна и убедительных напоминаний встать со стула.", ["GPS", "5 ATM", "10 дней"]),
            ("frame-monitor", "Frame 27 Studio", "workspace", 32990, "#c084fc", "F27", "27-дюймовый 4K-монитор с USB-C и заводской калибровкой цвета.", ["4K IPS", "USB-C 90 Вт", "sRGB 100%"]),
            ("key-one", "Key One Low", "workspace", 8490, "#facc15", "K1", "Низкопрофильная механическая клавиатура без офисного пулемёта.", ["Hot-swap", "2.4 ГГц", "75%"]),
            ("beam-speaker", "Beam Mini", "audio", 11990, "#fb7185", "BM", "Домашняя колонка с насыщенным звуком и мультирум-синхронизацией.", ["Wi-Fi", "AirPlay", "40 Вт"])
        };
        var usedByProduct = Enumerable.Repeat(0, 40).ToArray();
        for (var orderIndex = 0; orderIndex < 50; orderIndex++)
            usedByProduct[(orderIndex * 13 + 3) % 40]++;

        for (var index = 0; index < 40; index++)
        {
            var data = index < firstProducts.Length
                ? firstProducts[index]
                : ($"signal-{index + 1:00}", $"Signal Product {index + 1:00}", new[] { "audio", "computers", "wearables", "workspace" }[index % 4], 3990 + index * 1250, $"#{(0x345678 + index * 7919) & 0xffffff:X6}", $"S{index + 1:00}", $"Детерминированный демонстрационный товар Signal Store №{index + 1}.", new[] { "Demo", $"Series {index % 5 + 1}" });
            var finalStock = index == 3 ? 0 : 10 + index % 8;
            products.Add(new ProductEntity
            {
                Id = data.Item1, Name = data.Item2, Category = data.Item3, Price = data.Item4,
                Stock = finalStock + usedByProduct[index], ReservedStock = 0,
                LowStockThreshold = 2 + index % 4, Status = "published", Accent = data.Item5,
                Mark = data.Item6, Description = data.Item7, TagsJson = JsonSerializer.Serialize(data.Item8)
            });
        }
        db.Products.AddRange(products);
        await db.SaveChangesAsync();

        var start = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        for (var orderIndex = 0; orderIndex < 50; orderIndex++)
        {
            var productIndex = (orderIndex * 13 + 3) % 40;
            var created = start.AddDays(orderIndex);
            var product = products[productIndex];
            db.Orders.Add(new OrderEntity
            {
                Id = $"seed-order-{orderIndex + 1:000}", UserKey = StoreConstants.LocalUser, Status = "completed",
                CreatedAt = created, UpdatedAt = created.AddHours(2), ReservedAt = created.AddMinutes(10),
                CompletedAt = created.AddHours(1), Items = [new OrderItemEntity
                {
                    ProductId = product.Id, ProductName = product.Name, UnitPrice = product.Price, Quantity = 1
                }]
            });
        }
        await db.SaveChangesAsync();
    }
}

static class StoreConstants
{
    public const string LocalUser = "local-demo-user";
    public static readonly HashSet<string> ProductStatuses = new(StringComparer.Ordinal) { "draft", "published", "archived" };
    public static readonly HashSet<string> AllowedCategories = new(StringComparer.Ordinal) { "audio", "computers", "wearables", "workspace" };
    public static readonly SemaphoreSlim MutationGate = new(1, 1);
}
