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

        // --- BẢNG TƯƠNG TÁC DETAILS ---
        public DbSet<Comment> Comments { get; set; }
        public DbSet<Consultation> Consultations { get; set; }
        public DbSet<Appointment> Appointments { get; set; }
        public DbSet<Voucher> Vouchers { get; set; }
        public DbSet<UserMessage> UserMessages { get; set; }
        public DbSet<Role> Roles { get; set; }
        public DbSet<ProjectLead> ProjectLeads { get; set; }

        public DbSet<BusinessProfile> BusinessProfiles { get; set; }
        public DbSet<Notification> Notifications { get; set; }

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

            // Cấu hình Khóa chính cho Notification (đề phòng EF không tự nhận diện)
            modelBuilder.Entity<Notification>().HasKey(n => n.NotificationID);

            // ===== FIX WARNING DECIMAL =====
            modelBuilder.Entity<PostServicePackage>()
                .Property(p => p.Price)
                .HasPrecision(18, 2);

            modelBuilder.Entity<Voucher>()
                .Property(v => v.DiscountPercent)
                .HasPrecision(5, 2);

            modelBuilder.Entity<Voucher>()
                .Property(v => v.MaxDiscountAmount)
                .HasPrecision(18, 2);

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

            // Cấu hình Quan hệ cho Notification
            modelBuilder.Entity<Notification>()
                .HasOne(n => n.User)
                .WithMany()
                .HasForeignKey(n => n.UserID)
                .OnDelete(DeleteBehavior.Cascade); // Xóa user thì xóa luôn thông báo của họ

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

            // Bảng Comments
            modelBuilder.Entity<Comment>()
                .HasOne(c => c.User)
                .WithMany()
                .HasForeignKey(c => c.UserID)
                .OnDelete(DeleteBehavior.Restrict);

            // Cấu hình self-referencing cho Comment (Tránh lỗi vòng lặp)
            modelBuilder.Entity<Comment>()
                .HasOne(c => c.ParentComment)
                .WithMany(c => c.Replies)
                .HasForeignKey(c => c.ParentID)
                .OnDelete(DeleteBehavior.Restrict);
        }

        public IDbConnection CreateConnection()
        {
            return new SqlConnection(_configuration.GetConnectionString("DefaultConnection"));
        }
    }
}