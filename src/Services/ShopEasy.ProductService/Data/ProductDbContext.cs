using Microsoft.EntityFrameworkCore;

namespace ShopEasy.ProductService.Data;

public class Product
{
    public Guid   Id          { get; set; }
    public string Name        { get; set; } = default!;
    public string Description { get; set; } = default!;
    public decimal Price      { get; set; }
    public string Category    { get; set; } = default!;
    public string ImageUrl    { get; set; } = string.Empty;
    public bool   IsActive    { get; set; } = true;
    public string CreatedBy   { get; set; } = default!; // userId from token
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class ProductDbContext : DbContext
{
    public ProductDbContext(DbContextOptions<ProductDbContext> options) : base(options) { }

    public DbSet<Product> Products => Set<Product>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Product>(e =>
        {
            e.HasKey(p => p.Id);
            e.Property(p => p.Price).HasColumnType("decimal(18,2)");
            e.HasIndex(p => p.Category);
        });
    }
}

public static class ProductDbSeeder
{
    public static void Seed(ProductDbContext db)
    {
        if (db.Products.Any()) return;

        db.Products.AddRange(
            new Product
            {
                Id = Guid.Parse("00000000-0000-0000-0000-000000000001"),
                Name = "Wireless Headphones",
                Description = "Premium noise-cancelling headphones",
                Price = 299.99m,
                Category = "Electronics",
                CreatedBy = "system",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            },
            new Product
            {
                Id = Guid.Parse("00000000-0000-0000-0000-000000000002"),
                Name = "Mechanical Keyboard",
                Description = "TKL layout with Cherry MX switches",
                Price = 149.99m,
                Category = "Electronics",
                CreatedBy = "system",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            },
            new Product
            {
                Id = Guid.Parse("00000000-0000-0000-0000-000000000003"),
                Name = "Standing Desk",
                Description = "Motorized sit/stand desk, 140x70cm",
                Price = 599.99m,
                Category = "Furniture",
                CreatedBy = "system",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            },
            new Product
            {
                Id = Guid.Parse("00000000-0000-0000-0000-000000000004"),
                Name = "Ergonomic Office Chair",
                Description = "Lumbar support, adjustable armrests",
                Price = 449.99m,
                Category = "Furniture",
                CreatedBy = "system",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            },
            new Product
            {
                Id = Guid.Parse("00000000-0000-0000-0000-000000000005"),
                Name = "4K Webcam",
                Description = "1080p/60fps with built-in ring light",
                Price = 89.99m,
                Category = "Electronics",
                CreatedBy = "system",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            }
        );
        db.SaveChanges();
    }
}
