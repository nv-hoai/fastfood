using System.Security.Claims;
using FastFood.MVC.Data;
using FastFood.MVC.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace FastFood.MVC.Controllers
{
	[Authorize(Policy = "CustomerAccess")]
    public class CartController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly IAuthorizationService _authorization;

        public CartController(ApplicationDbContext context, IAuthorizationService authorization)
        {
            _context = context;
            _authorization = authorization;
        }

        //Lấy thông tin giỏ hàng của khách ID từ DB
        public async Task<List<CartItem>> GetCartAsync(int CustomerID)
        {
            var cart = await _context.CartItems
                                    .Include(c => c.Product)
                                    .Include(c => c.Promotion) // Include promotion details
                                    .Where(c => c.CustomerID == CustomerID)
                                    .ToListAsync();
            return cart;
        }

        //Hiển thị giỏ hàng sau khi đăng nhập
        [HttpGet]
        public async Task<IActionResult> Index()
        {
            var userID = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var customer = await _context.Customers.FirstOrDefaultAsync(c => c.UserID == userID);
			var cart = await GetCartAsync(customer!.CustomerID);
            return View(cart);
		}

        // Get details of a specific cart item
        public async Task<IActionResult> Details(int productID)
        {
            var userID = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var customer = await _context.Customers.FirstOrDefaultAsync(c => c.UserID == userID);

            var cartItem = await _context.CartItems
                .Include(c => c.Product)
                .Include(c => c.Promotion)
                .FirstOrDefaultAsync(c => c.CustomerID == customer.CustomerID && c.ProductID == productID);

            if (cartItem == null)
            {
                TempData["CartError"] = "Không tìm thấy sản phẩm trong giỏ hàng!";
                return RedirectToAction(nameof(Index));
            }

            // Load available promotions for dropdown
            ViewBag.PromotionID = new SelectList(
                await GetValidPromotionAsync(productID, cartItem.Product.CategoryID),
                "PromotionID",
                "Name",
                cartItem.PromotionID
            );

            return View(cartItem);
        }


        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddToCart(int productID, int quantity = 1)
        {
            if (quantity < 1)
            {
                return Json(new
                {
                    success = false,
                    message = "Số lượng sản phẩm phải lớn hơn 0."
                });
            }

            var userID = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var customer = await _context.Customers.FirstOrDefaultAsync(c => c.UserID == userID);

            var product = await _context.Products
                .Include(p => p.Category) // Include Category to get its ID
                .FirstOrDefaultAsync(p => p.ProductID == productID);

            if (product == null)
            {
                return Json(new
                {
                    success = false,
                    message = $"Không tìm thấy sản phẩm có mã #{productID}"
                });
            }

            var carts = await GetCartAsync(customer!.CustomerID);
            var existingItem = carts.FirstOrDefault(c => c.ProductID == productID);

            if (existingItem != null)
            {
                existingItem.Quantity += quantity;
                existingItem.Calculate();
                _context.CartItems.Update(existingItem);
            }
            else
            {
                // Find best promotion for this product
                var validPromotions = await GetValidPromotionAsync(productID, product.CategoryID);
                var bestPromotion = validPromotions.FirstOrDefault();

                var cartItem = new CartItem
                {
                    CustomerID = customer.CustomerID,
                    ProductID = productID,
                    ProductName = product.Name,
                    UnitPrice = product.Price,
                    Quantity = quantity,
                    CreatedAt = DateTime.Now,
                    // Apply promotion if found
                    PromotionID = bestPromotion?.PromotionID,
                    PromotionName = bestPromotion?.Name,
                    Promotion = bestPromotion
                };

                cartItem.Calculate();
                await _context.CartItems.AddAsync(cartItem);
            }

            await _context.SaveChangesAsync();

            return Json(new
            {
                success = true,
                message = $"Đã thêm sản phẩm [#{productID}-{product.Name}] vào giỏ hàng!",
                cartCount = carts.Sum(c => c.Quantity) + (existingItem == null ? quantity : 0)
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ApplyPromotion(int productID, int? promotionID)
        {
            var userID = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var customer = await _context.Customers.FirstOrDefaultAsync(c => c.UserID == userID);

            var cartItem = await _context.CartItems
                .Include(c => c.Product)
                .FirstOrDefaultAsync(c => c.CustomerID == customer.CustomerID && c.ProductID == productID);

            if (cartItem == null)
            {
                TempData["CartError"] = "Không tìm thấy sản phẩm trong giỏ hàng!";
                return RedirectToAction(nameof(Index));
            }

            // Update promotion
            if (promotionID.HasValue)
            {
                var promotion = await _context.Promotions.FindAsync(promotionID.Value);
                cartItem.PromotionID = promotionID;
                cartItem.PromotionName = promotion?.Name;
				cartItem.Promotion = promotion;
            }
            else
            {
                cartItem.PromotionID = null;
                cartItem.PromotionName = null;
				cartItem.Promotion = null;
            }

            // Recalculate prices
            cartItem.Calculate();
            _context.Update(cartItem);
            await _context.SaveChangesAsync();

            TempData["Success"] = "Đã cập nhật khuyến mãi cho sản phẩm!";
            return RedirectToAction(nameof(Details), new { productID });
        }

        //Xóa sản phẩm khỏi giỏ
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RemoveFromCart(int productID)
        {
			var userID = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var customer = await _context.Customers.FirstOrDefaultAsync(c => c.UserID == userID); 

            var carts = await GetCartAsync(customer.CustomerID);
            var cartItem = carts.FirstOrDefault(ci => ci.ProductID == productID);
			if (cartItem == null)
			{
				return Json(new
				{
					success = false,
					message = $"Không tìm thấy sản phẩm mã #{productID} trong giỏ hàng."
				});
			}

			_context.CartItems.Remove(cartItem);
            await _context.SaveChangesAsync();

			return Json(new
			{
				success = true,
				message = $"Xóa thành công sản phẩm mã [#{productID}-{cartItem.ProductName}] khỏi giỏ hàng!"
			});
		}

		//Cập nhật số lượng sản phẩm
		[HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateCart(int productID, int quantity)
		{
			var userID = User.FindFirstValue(ClaimTypes.NameIdentifier);
			var customer = await _context.Customers.FirstOrDefaultAsync(c => c.UserID == userID);

			var carts = await GetCartAsync(customer.CustomerID);
			var cartItem = carts.FirstOrDefault(ci => ci.ProductID == productID);

			if (cartItem == null)
			{
				return Json(new
				{
					success = false,
					message = $"Không tìm thấy sản phẩm mã #{productID} trong giỏ hàng."
				});
			}

			if (quantity <= 0)
			{
				_context.CartItems.Remove(cartItem);
				await _context.SaveChangesAsync();
				return Json(new
				{
					success = true,
					message = $"Đã xóa sản phẩm mã [#{productID}-{cartItem.ProductName}] vì số lượng nhỏ hơn hoặc bằng 0."
				});
			}

			cartItem.Quantity = quantity;
			_context.CartItems.Update(cartItem);
			await _context.SaveChangesAsync();

			return Json(new
			{
				success = true,
				message = $"Thành công thay đổi số lượng sản phẩm [#{productID}-{cartItem.ProductName}] thành {quantity}."
			});
		}

        //Xóa giỏ hàng
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ClearCart()
        {
			var userID = User.FindFirstValue(ClaimTypes.NameIdentifier);
			var customer = await _context.Customers.FirstOrDefaultAsync(c => c.UserID == userID);

			var carts = await GetCartAsync(customer.CustomerID);

			if (carts != null)
			{
				_context.CartItems.RemoveRange(carts);
			}

			await _context.SaveChangesAsync();
			return Json(new 
			{ 
				success = true,
				message = "Đã xóa thành công toàn bộ giỏ hàng",
				cartCount = 0
			});
        }
        [HttpGet]
        public async Task<IActionResult> GetCartCount()
        {
            var userID = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var customer = await _context.Customers.FirstOrDefaultAsync(c => c.UserID == userID);
            if (customer == null)
            {
                return Json(new { count = 0 });
            }

            var count = await _context.CartItems
                .Where(c => c.CustomerID == customer.CustomerID)
                .CountAsync();

            return Json(new { count });
        }

		[HttpPost]
		[ValidateAntiForgeryToken]
		public async Task<IActionResult> CreateCartNow(int productID)
		{
			var userID = User.FindFirstValue(ClaimTypes.NameIdentifier);
			var customer = await _context.Customers.FirstOrDefaultAsync(c => c.UserID == userID);

			var product = await _context.Products.FirstOrDefaultAsync(p => p.ProductID == productID);
			if (product == null)
			{
				return Json(new
				{
					success = false,
					messafe = $"Không tìm thấy sản phẩm #{productID}"
				});
			}

			var existingCartItem = await _context.CartItems.FirstOrDefaultAsync(ci =>
										ci.CustomerID == customer.CustomerID
										&& ci.ProductID == productID);

			if (existingCartItem != null)
			{
				existingCartItem.Quantity++;
				existingCartItem.Calculate();
				_context.CartItems.Update(existingCartItem);
			}
			else
			{
                // Find best promotion for this product
                var validPromotions = await GetValidPromotionAsync(productID, product.CategoryID);
                var bestPromotion = validPromotions.FirstOrDefault();

                var cartItem = new CartItem
                {
                    CustomerID = customer.CustomerID,
                    ProductID = productID,
                    ProductName = product.Name,
                    UnitPrice = product.Price,
                    Quantity = 1,
                    CreatedAt = DateTime.Now,
                    // Apply promotion if found
                    PromotionID = bestPromotion?.PromotionID,
                    PromotionName = bestPromotion?.Name,
                    Promotion = bestPromotion
                };
                cartItem.Calculate();
                _context.CartItems.Add(cartItem);
			}

			await _context.SaveChangesAsync();

			return Json(new
			{
				success = true,
			});
		}


		private async Task<List<Promotion>> GetValidPromotionAsync(int productID, int categoryID)
        {
            var now = DateTime.Now;

            var validPromotions = await _context.Promotions
                .Where(p =>
                    p.StartDate <= now && p.ExpiryDate >= now &&
                    (p.ProductID == productID || p.CategoryID == categoryID)
                )
                .OrderByDescending(p => p.DiscountPercent)
                .ToListAsync();

            return validPromotions;
        }
    }

}
