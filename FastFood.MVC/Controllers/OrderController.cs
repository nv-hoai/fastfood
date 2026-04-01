using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using FastFood.MVC.Models;
using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;
using FastFood.MVC.Services.Interfaces;
using FastFood.MVC.Data;
using Microsoft.EntityFrameworkCore;

namespace FastFood.MVC.Controllers
{
    [Authorize]
    public class OrderController : Controller
    {
        private readonly IOrderService _orderService;
        private readonly ApplicationDbContext _context;
        private readonly IAuthorizationService _authorization;

        public OrderController(
            IOrderService orderService,
            ApplicationDbContext context,
            IAuthorizationService authorization)
        {
            _orderService = orderService;
            _context = context;
            _authorization = authorization;
        }

        public async Task<IActionResult> Index(int? orderId, string orderStatus, string customerName, DateTime? startDate, DateTime? endDate)
        {
            // Create a SelectList for the OrderStatus enum for the dropdown
            ViewBag.Status = Enum.GetValues(typeof(OrderStatus))
                .Cast<OrderStatus>()
                .Select(e => new SelectListItem
                {
                    Value = e.ToString(),
                    Text = GetStatusDisplayName(e),
                    Selected = !string.IsNullOrEmpty(orderStatus) && e.ToString() == orderStatus
                }).ToList();

            // var test = ViewBag.Statu;

            // Parse orderStatus to the enum if provided
            OrderStatus? statusFilter = null;
            if (!string.IsNullOrEmpty(orderStatus) && Enum.TryParse<OrderStatus>(orderStatus, out var parsedStatus))
            {
                statusFilter = parsedStatus;
            }

            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var isAdmin = (await _authorization.AuthorizeAsync(User, "AdminAccess")).Succeeded;
            var isEmployee = (await _authorization.AuthorizeAsync(User, "EmployeeAccess")).Succeeded;
            var isShipper = (await _authorization.AuthorizeAsync(User, "ShipperAccess")).Succeeded;

            int? shipperId = null;
            if (isShipper)
            {
                var shipper = await _context.Shippers.FirstOrDefaultAsync(s => s.UserID == userId);
                shipperId = shipper?.ShipperID;
            }

            // Build query parameters
            var parameters = new OrderQueryParameters
            {
                OrderId = orderId,
                UserId = userId,
                Status = statusFilter,
                CustomerName = customerName,
                StartDate = startDate,
                EndDate = endDate,
                IsAdmin = isAdmin,
                IsEmployee = isEmployee,
                IsShipper = isShipper,
                ShipperId = shipperId
            };

            // Get orders using service
            var orders = await _orderService.GetOrdersAsync(parameters);
            return View(orders);
        }

        public async Task<IActionResult> Details(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var isAdmin = (await _authorization.AuthorizeAsync(User, "AdminAccess")).Succeeded;
            var isEmployee = (await _authorization.AuthorizeAsync(User, "EmployeeAccess")).Succeeded;
            var isShipper = (await _authorization.AuthorizeAsync(User, "ShipperAccess")).Succeeded;

            var order = await _orderService.GetOrderByIdAsync(id.Value, userId, isAdmin, isEmployee, isShipper);

            if (order == null)
            {
                return NotFound();
            }

            return View(order);
        }

        [Authorize(Policy = "AdminAccess")]
        public IActionResult Create()
        {
            Order model = new Order();
            ViewData["CustomerID"] = new SelectList(_context.Customers, "CustomerID", "CustomerID");
            ViewData["EmployeeID"] = new SelectList(_context.Employees, "EmployeeID", "EmployeeID");
            ViewData["ShipperID"] = new SelectList(_context.Shippers, "ShipperID", "ShipperID");
            return View(model);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Policy = "AdminAccess")]
        public async Task<IActionResult> Create(Order order)
        {
            if (ModelState.IsValid)
            {
                await _orderService.CreateOrderAsync(order);
                return RedirectToAction(nameof(Index));
            }
            ViewData["CustomerID"] = new SelectList(_context.Customers, "CustomerID", "CustomerID", order.CustomerID);
            ViewData["EmployeeID"] = new SelectList(_context.Employees, "EmployeeID", "EmployeeID", order.EmployeeID);
            ViewData["ShipperID"] = new SelectList(_context.Shippers, "ShipperID", "ShipperID", order.ShipperID);
            return View(order);
        }

        [Authorize(Policy = "AdminAccess")]
        public async Task<IActionResult> Edit(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var order = await _orderService.GetOrderByIdAsync(id.Value, userId, isAdmin: true);
            if (order == null)
            {
                return NotFound();
            }
            ViewData["CustomerID"] = new SelectList(_context.Customers, "CustomerID", "CustomerID", order.CustomerID);
            ViewData["EmployeeID"] = new SelectList(_context.Employees, "EmployeeID", "EmployeeID", order.EmployeeID);
            ViewData["ShipperID"] = new SelectList(_context.Shippers, "ShipperID", "ShipperID", order.ShipperID);
            return View(order);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Policy = "AdminAccess")]
        public async Task<IActionResult> Edit(int id, Order order)
        {
            if (id != order.OrderID)
            {
                return NotFound();
            }

            if (ModelState.IsValid)
            {
                var result = await _orderService.UpdateOrderAsync(order);

                if (!result.Success)
                {
                    if (result.CurrentOrder != null)
                    {
                        // Update the RowVersion to the current database value
                        order.RowVersion = result.CurrentOrder.RowVersion;
                        ModelState.AddModelError("", result.ErrorMessage);
                    }
                    else
                    {
                        return NotFound(result.ErrorMessage);
                    }
                }
                else
                {
                    return RedirectToAction(nameof(Index));
                }
            }

            ViewData["CustomerID"] = new SelectList(_context.Customers, "CustomerID", "CustomerID", order.CustomerID);
            ViewData["EmployeeID"] = new SelectList(_context.Employees, "EmployeeID", "EmployeeID", order.EmployeeID);
            ViewData["ShipperID"] = new SelectList(_context.Shippers, "ShipperID", "ShipperID", order.ShipperID);
            return View(order);
        }

        [Authorize(Policy = "AdminAccess")]
        public async Task<IActionResult> Delete(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var order = await _orderService.GetOrderByIdAsync(id.Value, userId, isAdmin: true);
            if (order == null)
            {
                return NotFound();
            }

            return View(order);
        }

        [HttpPost, ActionName("Delete")]
        [ValidateAntiForgeryToken]
        [Authorize(Policy = "AdminAccess")]
        public async Task<IActionResult> DeleteConfirmed(int id)
        {
            await _orderService.DeleteOrderAsync(id);
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Policy = "CustomerAccess")]
        public async Task<IActionResult> CreateFromCart(string address, ShippingMethod shippingMethod)
        {
            var userID = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var customer = await _context.Customers.FirstOrDefaultAsync(c => c.UserID == userID);

            if (customer == null)
            {
                return Json(new { success = false, message = "Customer not found" });
            }

            var order = await _orderService.CreateOrderFromCartAsync(customer.CustomerID, address, shippingMethod);

            if (order == null)
            {
                return Json(new { success = false, message = "Giỏ hàng trống." });
            }

            return Json(new { success = true });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Cancel(int orderID, string note)
        {
            var userID = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var isAdminOrEmployee = (await _authorization.AuthorizeAsync(User, "AdminOrEmployeeAccess")).Succeeded;

            var result = await _orderService.CancelOrderAsync(orderID, userID, note, isAdminOrEmployee);

            if (!result.Success)
            {
                return Json(new { success = false, message = result.ErrorMessage });
            }

            return Json(new { success = true, message = $"Đơn hàng #{orderID} đã được hủy." });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Policy = "EmployeeAccess")]
        public async Task<IActionResult> AcceptOrder(int orderID)
        {
            var userID = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var employee = await _context.Employees.FirstOrDefaultAsync(e => e.UserID == userID);

            if (employee == null)
            {
                return Json(new { success = false, message = "Employee profile not found" });
            }

            var result = await _orderService.AcceptOrderAsync(orderID, employee.EmployeeID);

            if (!result.Success)
            {
                return Json(new { success = false, message = result.ErrorMessage });
            }

            return Json(new { success = true, message = $"Order #{orderID} has been accepted and is now being processed" });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Policy = "EmployeeAccess")]
        public async Task<IActionResult> MarkAsPrepared(int orderID)
        {
            var result = await _orderService.MarkOrderAsPreparedAsync(orderID);

            if (!result.Success)
            {
                return Json(new { success = false, message = result.ErrorMessage });
            }

            return Json(new { success = true, message = $"Order #{orderID} is now ready for delivery" });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Policy = "ShipperAccess")]
        public async Task<IActionResult> AcceptDelivery(int orderID)
        {
            var userID = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var shipper = await _context.Shippers.FirstOrDefaultAsync(s => s.UserID == userID);

            if (shipper == null)
            {
                return Json(new { success = false, message = "Shipper profile not found" });
            }

            var result = await _orderService.AcceptDeliveryAsync(orderID, shipper.ShipperID);

            if (!result.Success)
            {
                return Json(new { success = false, message = result.ErrorMessage });
            }

            return Json(new { success = true, message = $"You have accepted order #{orderID} for delivery" });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Policy = "ShipperAccess")]
        public async Task<IActionResult> MarkAsDelivered(int orderID)
        {
            var userID = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var shipper = await _context.Shippers.FirstOrDefaultAsync(s => s.UserID == userID);

            if (shipper == null)
            {
                return Json(new { success = false, message = "Shipper profile not found" });
            }

            var result = await _orderService.MarkOrderAsDeliveredAsync(orderID, shipper.ShipperID);

            if (!result.Success)
            {
                return Json(new { success = false, message = result.ErrorMessage });
            }

            return Json(new { success = true, message = $"Order #{orderID} has been marked as delivered" });
        }

        private string GetStatusDisplayName(OrderStatus status)
        {
            return status switch
            {
                OrderStatus.Pending => "Chờ xác nhận",
                OrderStatus.Processing => "Đang chế biến",
                OrderStatus.Prepared => "Sẵn sàng giao",
                OrderStatus.Delivering => "Đang giao hàng",
                OrderStatus.Completed => "Hoàn thành",
                OrderStatus.Cancelled => "Đã hủy",
                _ => status.ToString()
            };
        }
    }
}
