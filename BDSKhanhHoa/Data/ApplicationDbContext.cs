using Microsoft.EntityFrameworkCore;
using BDSKhanhHoa.Models;
using System.Data;
using Microsoft.Data.SqlClient;

namespace BDSKhanhHoa.Data
{
    public class ApplicationDbContext : DbContext
    {
        private readonly IConfiguration _configuration;

        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options, IConfiguration configuration)
            : base(options)
        {
            _configuration = configuration;
        }

        // --- CÁC BẢNG CỐT LÕI ---
        public DbSet<User> Users { get; set; }
        public DbSet<Property> Properties { get; set; }
        public DbSet<Area> Areas { get; set; }
        public DbSet<Ward> Wards { get; set; }
        public DbSet<PropertyType> PropertyTypes { get; set; }
        public DbSet<Blog> Blogs { get; set; }
        public DbSet<ContactMessage> ContactMessages { get; set; }
        public DbSet<Favorite> Favorites { get; set; }
        public DbSet<PropertyFeature> PropertyFeatures { get; set; }
        public DbSet<AuditLog> AuditLogs { get; set; }
        public DbSet<Transaction> Transactions { get; set; }
        public DbSet<UserViolation> UserViolations { get; set; }
        public DbSet<Banner> Banners { get; set; }
        public DbSet<PostServicePackage> PostServicePackages { get; set; }
        public DbSet<PropertyImage> PropertyImages { get; set; }
        public DbSet<ChatLogs> ChatLogs { get; set; }
        public DbSet<PropertyReport> PropertyReports { get; set; }
        public DbSet<Project> Projects { get; set; }

        // --- 4 BẢNG TƯƠNG TÁC DETAILS (SỬA LẠI TÊN SỐ NHIỀU) ---
        public DbSet<Comment> Comments { get; set; }
        public DbSet<Consultation> Consultations { get; set; }
        public DbSet<Appointment> Appointments { get; set; }
        public DbSet<CoBrokerRequest> CoBrokerRequests { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // 1. Cấu hình Khóa chính (Primary Keys)
            modelBuilder.Entity<User>().HasKey(u => u.UserID);
            modelBuilder.Entity<Property>().HasKey(p => p.PropertyID);
            modelBuilder.Entity<Area>().HasKey(a => a.AreaID);
            modelBuilder.Entity<Ward>().HasKey(w => w.WardID);
            modelBuilder.Entity<PropertyType>().HasKey(pt => pt.TypeID);
            modelBuilder.Entity<ContactMessage>().HasKey(c => c.ContactID);
            modelBuilder.Entity<Blog>().HasKey(b => b.BlogID);
            modelBuilder.Entity<RoleUpgradeRequest>().HasKey(r => r.RequestID);
            modelBuilder.Entity<AuditLog>().HasKey(al => al.LogID);
            modelBuilder.Entity<Transaction>().HasKey(t => t.TransactionID);
            modelBuilder.Entity<UserViolation>().HasKey(uv => uv.ViolationID);
            modelBuilder.Entity<PropertyImage>().HasKey(pi => pi.ImageID);
            modelBuilder.Entity<Banner>().HasKey(b => b.BannerID);

            // Cấu hình kiểu Decimal
            modelBuilder.Entity<Property>().Property(p => p.Price).HasColumnType("decimal(18,2)");
            modelBuilder.Entity<Property>().Property(p => p.AreaSize).HasColumnType("decimal(18,2)");
            modelBuilder.Entity<Transaction>().Property(t => t.Amount).HasColumnType("decimal(18,2)");

            // Cấu hình Quan hệ cho Properties
            modelBuilder.Entity<Property>()
                .HasOne(p => p.User)
                .WithMany()
                .HasForeignKey(p => p.UserID)
                .OnDelete(DeleteBehavior.Restrict);

            // Cấu hình Quan hệ cho PropertyType (Cha - Con)
            modelBuilder.Entity<PropertyType>()
                .HasOne<PropertyType>()
                .WithMany()
                .HasForeignKey(pt => pt.ParentID)
                .OnDelete(DeleteBehavior.Restrict);

            // Cấu hình vòng lặp xóa (Cascade Delete)
            modelBuilder.Entity<AuditLog>()
                .HasOne(al => al.User).WithMany().HasForeignKey(al => al.UserID).OnDelete(DeleteBehavior.Cascade);
            modelBuilder.Entity<Transaction>()
                .HasOne(t => t.User).WithMany().HasForeignKey(t => t.UserID).OnDelete(DeleteBehavior.Cascade);
            modelBuilder.Entity<UserViolation>()
                .HasOne(uv => uv.User).WithMany().HasForeignKey(uv => uv.UserID).OnDelete(DeleteBehavior.Cascade);

            // =========================================================
            // CẤU HÌNH KHÓA NGOẠI CHO CÁC BẢNG MỚI ĐỂ TRÁNH LỖI VÒNG LẶP
            // =========================================================

            // Bảng Appointments (Có 2 liên kết về bảng User)
            modelBuilder.Entity<Appointment>()
                .HasOne(a => a.Buyer)
                .WithMany()
                .HasForeignKey(a => a.BuyerID)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<Appointment>()
                .HasOne(a => a.Seller)
                .WithMany()
                .HasForeignKey(a => a.SellerID)
                .OnDelete(DeleteBehavior.Restrict);

            // Bảng CoBrokerRequests (Có 2 liên kết về bảng User)
            modelBuilder.Entity<CoBrokerRequest>()
                .HasOne(c => c.Owner)
                .WithMany()
                .HasForeignKey(c => c.OwnerID)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<CoBrokerRequest>()
                .HasOne(c => c.Requester)
                .WithMany()
                .HasForeignKey(c => c.RequesterID)
                .OnDelete(DeleteBehavior.Restrict);

            // Bảng Comments
            modelBuilder.Entity<Comment>()
                .HasOne(c => c.User)
                .WithMany()
                .HasForeignKey(c => c.UserID)
                .OnDelete(DeleteBehavior.Restrict);
        }

        public IDbConnection CreateConnection()
        {
            return new SqlConnection(_configuration.GetConnectionString("DefaultConnection"));
        }
    }
}