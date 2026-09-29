using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    await StoreConstants.MutationGate.WaitAsync();
    try
    {
        await AbandonStaleCartsAsync(db);
        if (!TryParseDate(from, DateTime.MinValue, inclusiveEnd: false, out var fromDate) ||
            !TryParseDate(to, DateTime.MaxValue, inclusiveEnd: true, out var toDate))
            return BadRequest("from and to must be valid dates");
        if (fromDate > toDate)
            return BadRequest("from must not be later than to");

        var orders = await db.Orders.AsNoTracking().Include(order => order.Items)
            .Where(order => order.Status == "completed" && order.CompletedAt != null &&
                            order.CompletedAt >= fromDate && order.CompletedAt <= toDate)
            .OrderByDescending(order => order.CompletedAt).ToListAsync();
        var productStats = orders.SelectMany(order => order.Items)
            .GroupBy(item => item.ProductId)
            .Select(group => new
            {
                productId = group.Key,
                name = group.OrderBy(item => item.ProductName).Select(item => item.ProductName).FirstOrDefault() ?? group.Key,
                quantity = group.Sum(item => (long)item.Quantity),
                revenue = group.Sum(item => (long)item.UnitPrice * item.Quantity),
                units = group.Sum(item => (long)item.Quantity)
            })
            .OrderBy(item => item.name)
            .ToList();
        var revenue = productStats.Sum(item => item.revenue);
        return Results.Ok(new
        {
            from = fromDate,
            to = toDate,
            completedOrders = orders.Count,
            orderCount = orders.Count,
            revenue,
            totalRevenue = revenue,
            products = productStats,
            items = productStats
        });
    }
    finally
    {
        StoreConstants.MutationGate.Release();
    }
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

static bool TryParseDate(string? value, DateTime fallback, bool inclusiveEnd, out DateTime result)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        result = fallback;
        return true;
    }
    if (!DateTime.TryParse(value, CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out result))
        return false;
    // RFC query parameters are dates. A date-only `to` value includes that whole day.
    if (inclusiveEnd && !value.Contains(':') && !value.Contains('T', StringComparison.OrdinalIgnoreCase))
        result = result.Date.AddDays(1).AddTicks(-1);
    return true;
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
