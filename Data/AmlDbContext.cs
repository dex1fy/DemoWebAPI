using Microsoft.EntityFrameworkCore;

namespace DemoWebAPI.Data;

public sealed class AmlDbContext(DbContextOptions<AmlDbContext> options) : DbContext(options);
