#pragma warning disable
using FastFood.MVC.Data;
using FastFood.MVC.Models;
using FastFood.MVC.Services.Interfaces;
using Microsoft.EntityFrameworkCore;


namespace FastFood.MVC.Services
{
    public class OrderService : IOrderService
    {
        private readonly ApplicationDbContext _context;
        private readonly NotificationService _notificationService;

        public OrderService(ApplicationDbContext context, NotificationService notificationService)
        {
            _context = context;
            _notificationService = notificationService;
        }

        public async Task<IEnumerable<Order>> GetOrdersAsync(OrderQueryParameters parameters)
        {
            var query = _context.Orders.AsQueryable();
            
            // Include related entities
            query = query.Include(o => o.Customer)
                          .ThenInclude(c => c.User);
                          
            if (parameters.IsAdmin || parameters.IsEmployee || parameters.IsShipper)
            {
                query = query.Include(o => o.Employee)
                            .ThenInclude(e => e.User)
                            .Include(o => o.Shipper)
                            .ThenInclude(s => s.User);
            }

            // Apply filters based on user role
            if (!parameters.IsAdmin && !parameters.IsEmployee)
            {
                if (parameters.IsShipper && parameters.ShipperId.HasValue)
                {
                    // Shipper can see prepared orders and orders they're delivering/delivered
                    query = query.Where(o => 
                        o.Status == OrderStatus.Prepared || 
                        ((o.Status == OrderStatus.Delivering || o.Status == OrderStatus.Completed) 
                            && o.ShipperID == parameters.ShipperId));
                }
                else
                {
                    // Customer can only see their own orders
                    query = query.Where(o => o.Customer.UserID == parameters.UserId);
                }
            }

            // Apply common filters
            if (parameters.OrderId.HasValue)
            {
                query = query.Where(o => o.OrderID == parameters.OrderId.Value);
            }

            if (parameters.Status.HasValue)
            {
                // For shippers, we need special status filtering
                if (parameters.IsShipper && parameters.ShipperId.HasValue)
                {
                    if (parameters.Status == OrderStatus.Prepared)
                    {
                        query = query.Where(o => o.Status == OrderStatus.Prepared);
                    }
                    else
                    {
                        query = query.Where(o => o.Status == parameters.Status && o.ShipperID == parameters.ShipperId);
                    }
                }
                else
                {
                    query = query.Where(o => o.Status == parameters.Status);
                }
            }

            if (!string.IsNullOrEmpty(parameters.CustomerName) && (parameters.IsAdmin || parameters.IsEmployee || parameters.IsShipper))
            {
                query = query.Where(o => o.Customer.User.FullName.Contains(parameters.CustomerName));
            }

            if (parameters.StartDate.HasValue)
            {
                query = query.Where(o => o.CreatedAt.Date >= parameters.StartDate.Value.Date);
            }

            if (parameters.EndDate.HasValue)
            {
                query = query.Where(o => o.CreatedAt.Date <= parameters.EndDate.Value.Date);
            }

            // Order by creation date, newest first
            return await query.OrderByDescending(o => o.CreatedAt).ToListAsync();
        }

        public async Task<Order> GetOrderByIdAsync(int id, string userId, bool isAdmin = false, bool isEmployee = false, bool isShipper = false)
        {
            if (isAdmin || isEmployee)
            {
                // Admin/Employee can see all order details
                return await _context.Orders
                    .Include(o => o.Customer)
                        .ThenInclude(c => c.User)
                    .Include(o => o.Employee)
                        .ThenInclude(e => e.User)
                    .Include(o => o.Shipper)
                        .ThenInclude(s => s.User)
                    .Include(o => o.OrderDetails)
                        .ThenInclude(o => o.Product)
                    .Include(o => o.OrderDetails)
                        .ThenInclude(o => o.Promotion)
                    .FirstOrDefaultAsync(o => o.OrderID == id);
            }
            
            if (isShipper)
            {
                var shipper = await _context.Shippers.FirstOrDefaultAsync(s => s.UserID == userId);
                if (shipper == null) return null;
                
                // Shipper can see orders that are prepared or that they are delivering
                return await _context.Orders
                    .Include(o => o.Customer)
                        .ThenInclude(c => c.User)
                    .Include(o => o.Employee)
                        .ThenInclude(e => e.User)
                    .Include(o => o.Shipper)
                        .ThenInclude(s => s.User)
                    .Include(o => o.OrderDetails)
                        .ThenInclude(o => o.Product)
                    .FirstOrDefaultAsync(o => 
                        o.OrderID == id && (
                            o.Status == OrderStatus.Prepared ||
                            ((o.Status == OrderStatus.Delivering || o.Status == OrderStatus.Completed) && o.ShipperID == shipper.ShipperID)
                        ));
            }
            
            // Customer can only see their own orders
            return await _context.Orders
                .Include(o => o.Customer)
                    .ThenInclude(c => c.User)
                .Include(o => o.OrderDetails)
                    .ThenInclude(o => o.Product)
                .FirstOrDefaultAsync(o => o.OrderID == id && o.Customer.UserID == userId);
        }

        public async Task<bool> OrderExistsAsync(int id)
        {
            return await _context.Orders.AnyAsync(e => e.OrderID == id);
        }

        public async Task<Order> CreateOrderFromCartAsync(int customerId, string address, ShippingMethod shippingMethod)
        {
            var carts = await _context.CartItems
                .Include(c => c.Promotion)
                .Where(c => c.CustomerID == customerId)
                .ToListAsync();

            if (carts == null || !carts.Any())
            {
                return null;
            }

            var order = new Order
            {
                CustomerID = customerId,
                Address = address,
                ShippingMethod = shippingMethod,
                CreatedAt = DateTime.Now,
                Status = OrderStatus.Pending
            };

            foreach (var item in carts)
            {
                var orderDetail = new OrderDetail
                {
                    OrderID = order.OrderID,
                    ProductID = item.ProductID,
                    ProductName = item.ProductName,
                    UnitPrice = item.UnitPrice,
                    Quantity = item.Quantity,
                    PromotionID = item.PromotionID,
                    PromotionName = item.PromotionName,
                    Promotion = item.Promotion,
                };

                orderDetail.CalculatePrices();
                order.OrderDetails.Add(orderDetail);
            }

            order.CalculateTotalCharge();

            _context.Orders.Add(order);
            _context.CartItems.RemoveRange(carts);
            await _context.SaveChangesAsync();

            var employees = await _context.Employees.ToListAsync();
            foreach (var employee in employees)
            {
                await _notificationService.CreateNotification(
                    employee.UserID,
                    $"Có đơn hàng mới #{order.OrderID} cần xử lý.",
                    $"/Order/Details/{order.OrderID}",
                    "fa-check-circle"
                );
            }

            return order;
        }

        public async Task<(bool Success, string ErrorMessage, Order CurrentOrder)> CancelOrderAsync(int orderId, string userId, string note, bool isAdminOrEmployee = false)
        {
            Order order;

            if (isAdminOrEmployee)
            {
                order = await _context.Orders
                    .Include(o => o.Customer)
                    .FirstOrDefaultAsync(o => o.OrderID == orderId);
            }
            else
            {
                var customer = await _context.Customers.FirstOrDefaultAsync(c => c.UserID == userId);
                if (customer == null)
                    return (false, "Customer not found", null);

                order = await _context.Orders.FirstOrDefaultAsync(o =>
                    o.OrderID == orderId && o.CustomerID == customer.CustomerID && o.Status == OrderStatus.Pending);
            }

            if (order == null)
                return (false, $"Order #{orderId} not found", null);

            if (order.Status == OrderStatus.Cancelled)
                return (false, $"Order #{orderId} is already cancelled", order);

            order.Status = OrderStatus.Cancelled;
            order.CancelledAt = DateTime.Now;
            order.Note = note;

            try
            {
                if (isAdminOrEmployee)
                {
                    await _notificationService.CreateNotification(
                        order.Customer.UserID,
                        $"Đơn hàng #{orderId} đã bị hủy với lí do {note}.",
                        $"/Order/Details/{orderId}",
                        "fa-check-circle"
                    );
                }

                await _context.SaveChangesAsync();
                return (true, string.Empty, order);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Get the current database values
                var entry = _context.Entry(order);
                await entry.ReloadAsync();

                // Check if the order state still allows cancellation
                if (order.Status != OrderStatus.Pending && !isAdminOrEmployee)
                    return (false, "Order cannot be cancelled because it's already being processed", order);

                // Try updating again with reloaded entity
                order.Status = OrderStatus.Cancelled;
                order.CancelledAt = DateTime.Now;
                order.Note = note;

                try
                {
                    await _context.SaveChangesAsync();
                    return (true, string.Empty, order);
                }
                catch
                {
                    return (false, "Concurrency conflict when cancelling order. Please try again.", order);
                }
            }
        }

        public async Task<(bool Success, string ErrorMessage, Order CurrentOrder)> AcceptOrderAsync(int orderId, int employeeId)
        {
            var order = await _context.Orders
                .Include(o => o.Customer)
                .FirstOrDefaultAsync(o => o.OrderID == orderId && o.Status == OrderStatus.Pending);

            if (order == null)
                return (false, $"Order #{orderId} not found or not in Pending state", null);

            order.EmployeeID = employeeId;
            order.Status = OrderStatus.Processing;
            order.ProcessingAt = DateTime.Now;

            try
            {
                await _notificationService.CreateNotification(
                    order.Customer.UserID,
                    $"Đơn hàng #{orderId} đã được xác nhận và đang chuẩn bị.",
                    $"/Order/Details/{orderId}",
                    "fa-check-circle"
                );

                await _context.SaveChangesAsync();
                return (true, string.Empty, order);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Get the current database values
                var entry = _context.Entry(order);
                await entry.ReloadAsync();

                // Check if the order is still in a state that allows acceptance
                if (order.Status != OrderStatus.Pending)
                    return (false, $"Order #{orderId} can no longer be accepted because its status has changed", order);

                // Try updating again with reloaded entity
                order.EmployeeID = employeeId;
                order.Status = OrderStatus.Processing;
                order.ProcessingAt = DateTime.Now;

                try
                {
                    await _context.SaveChangesAsync();
                    return (true, string.Empty, order);
                }
                catch
                {
                    return (false, "Concurrency conflict when accepting order. Please try again.", order);
                }
            }
        }

        public async Task<(bool Success, string ErrorMessage, Order CurrentOrder)> MarkOrderAsPreparedAsync(int orderId)
        {
            var order = await _context.Orders
                .Include(o => o.Customer)
                .FirstOrDefaultAsync(o => o.OrderID == orderId && o.Status == OrderStatus.Processing);

            if (order == null)
                return (false, $"Order #{orderId} not found or not in Processing state", null);

            order.Status = OrderStatus.Prepared;
            order.PreparedAt = DateTime.Now;

            try
            {
                await _notificationService.CreateNotification(
                    order.Customer.UserID,
                    $"Đơn hàng #{orderId} đã được chuẩn bị xong và sắp được giao.",
                    $"/Order/Details/{orderId}",
                    "fa-check-circle"
                );

                var shippers = await _context.Shippers.ToListAsync();
                foreach (var shipper in shippers)
                {
                    await _notificationService.CreateNotification(
                        shipper.UserID,
                        $"Có đơn hàng #{orderId} có thể nhận.",
                        $"/Order/Details/{orderId}",
                        "fa-check-circle"
                    );
                }

                await _context.SaveChangesAsync();
                return (true, string.Empty, order);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Get the current database values
                var entry = _context.Entry(order);
                await entry.ReloadAsync();

                // Check if the order is still in a state that allows marking as prepared
                if (order.Status != OrderStatus.Processing)
                    return (false, $"Order #{orderId} cannot be marked as prepared because its status has changed", order);

                // Try updating again with reloaded entity
                order.Status = OrderStatus.Prepared;
                order.PreparedAt = DateTime.Now;

                try
                {
                    await _context.SaveChangesAsync();
                    return (true, string.Empty, order);
                }
                catch
                {
                    return (false, "Concurrency conflict when marking order as prepared. Please try again.", order);
                }
            }
        }

        public async Task<(bool Success, string ErrorMessage, Order CurrentOrder)> AcceptDeliveryAsync(int orderId, int shipperId)
        {
            var order = await _context.Orders
                .Include(o => o.Customer)
                .Include(o => o.Employee)
                .FirstOrDefaultAsync(o => o.OrderID == orderId && o.Status == OrderStatus.Prepared);

            if (order == null)
                return (false, $"Order #{orderId} not found or not in Prepared state", null);

            order.ShipperID = shipperId;
            order.Status = OrderStatus.Delivering;
            order.DeliveringAt = DateTime.Now;

            try
            {
                await _notificationService.CreateNotification(
                    order.Customer.UserID,
                    $"Đơn hàng #{orderId} đang được vận chuyển đến khách hàng.",
                    $"/Order/Details/{orderId}",
                    "fa-check-circle"
                );

                await _notificationService.CreateNotification(
                    order.Employee!.UserID,
                    $"Đơn hàng #{orderId} đang được vận chuyển đến khách hàng.",
                    $"/Order/Details/{orderId}",
                    "fa-check-circle"
                );

                await _context.SaveChangesAsync();
                return (true, string.Empty, order);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Get the current database values
                var entry = _context.Entry(order);
                await entry.ReloadAsync();

                // Check if the order is still in a state that allows delivery acceptance
                if (order.Status != OrderStatus.Prepared)
                    return (false, $"Order #{orderId} cannot be accepted for delivery because its status has changed", order);

                // Try updating again with reloaded entity
                order.ShipperID = shipperId;
                order.Status = OrderStatus.Delivering;
                order.DeliveringAt = DateTime.Now;

                try
                {
                    await _context.SaveChangesAsync();
                    return (true, string.Empty, order);
                }
                catch
                {
                    return (false, "Concurrency conflict when accepting order for delivery. Please try again.", order);
                }
            }
        }

        public async Task<(bool Success, string ErrorMessage, Order CurrentOrder)> MarkOrderAsDeliveredAsync(int orderId, int shipperId)
        {
            var order = await _context.Orders
                .Include(o => o.Customer)
                .Include(o => o.Employee)
                .FirstOrDefaultAsync(o =>
                    o.OrderID == orderId &&
                    o.Status == OrderStatus.Delivering &&
                    o.ShipperID == shipperId);

            if (order == null)
                return (false, $"Order #{orderId} not found or not in Delivering state", null);

            order.Status = OrderStatus.Completed;
            order.CompletedAt = DateTime.Now;

            try
            {
                await _notificationService.CreateNotification(
                    order.Customer.UserID,
                    $"Đơn hàng #{orderId} đã hoàn thành.",
                    $"/Order/Details/{orderId}",
                    "fa-check-circle"
                );

                await _notificationService.CreateNotification(
                    order.Employee!.UserID,
                    $"Đơn hàng #{orderId} đã hoàn thành.",
                    $"/Order/Details/{orderId}",
                    "fa-check-circle"
                );

                var orderDetails = await _context.OrderDetails
                    .Include(od => od.Product)
                    .Where(od => od.OrderID == orderId)
                    .ToListAsync();

                foreach (var orderDetail in orderDetails)
                {
                    var product = await _context.Products.FindAsync(orderDetail.ProductID);
                    if (product != null)
                    {
                        product.SoldQuantity += orderDetail.Quantity;
                    }
                }

                await _context.SaveChangesAsync();
                return (true, string.Empty, order);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Get the current database values
                var entry = _context.Entry(order);
                await entry.ReloadAsync();

                // Check if the order is still in a state that allows marking as delivered
                if (order.Status != OrderStatus.Delivering || order.ShipperID != shipperId)
                    return (false, $"Order #{orderId} cannot be marked as delivered because its status or assignment has changed", order);

                // Try updating again with reloaded entity
                order.Status = OrderStatus.Completed;
                order.CompletedAt = DateTime.Now;

                try
                {
                    await _context.SaveChangesAsync();
                    return (true, string.Empty, order);
                }
                catch
                {
                    return (false, "Concurrency conflict when marking order as delivered. Please try again.", order);
                }
            }
        }

        public async Task<Order> CreateOrderAsync(Order order)
        {
            _context.Add(order);
            await _context.SaveChangesAsync();
            return order;
        }

        public async Task<(bool Success, string ErrorMessage, Order CurrentOrder)> UpdateOrderAsync(Order order)
        {
            try
            {
                _context.Update(order);
                await _context.SaveChangesAsync();
                return (true, string.Empty, order);
            }
            catch (DbUpdateConcurrencyException ex)
            {
                if (!await OrderExistsAsync(order.OrderID))
                {
                    return (false, "Order no longer exists", null);
                }

                // Get the current database values
                var entry = ex.Entries.Single();
                var databaseValues = await entry.GetDatabaseValuesAsync();

                if (databaseValues == null)
                {
                    return (false, "Order has been deleted", null);
                }

                // Create a fresh entity with database values
                var databaseOrder = (Order)databaseValues.ToObject();

                // Return the current database values along with error message
                return (false, "This order has been modified by another user. The database values are shown below.", databaseOrder);
            }
        }

        public async Task<bool> DeleteOrderAsync(int orderId)
        {
            var order = await _context.Orders.FindAsync(orderId);
            if (order == null) return false;
            
            _context.Orders.Remove(order);
            await _context.SaveChangesAsync();
            return true;
        }
    }
}
