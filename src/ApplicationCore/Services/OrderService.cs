using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Threading.Tasks;
using Ardalis.GuardClauses;
using Azure;
using Azure.Identity;
using Azure.Messaging.EventGrid;
using Microsoft.Azure.ServiceBus;
using Microsoft.eShopWeb.ApplicationCore.Entities;
using Microsoft.eShopWeb.ApplicationCore.Entities.BasketAggregate;
using Microsoft.eShopWeb.ApplicationCore.Entities.OrderAggregate;
using Microsoft.eShopWeb.ApplicationCore.Interfaces;
using Microsoft.eShopWeb.ApplicationCore.Specifications;

namespace Microsoft.eShopWeb.ApplicationCore.Services;

public class OrderDetails
{
    public string ShippingAddress { get; set; }
    public decimal TotalPrice { get; set; }
    public IEnumerable<OrderItemDetails> OrderItems { get; set; }

}

public class OrderItemDetails
{
    public string Name { get; set; }
    public decimal UnitPrice { get; set; }
}

public class OrderService : IOrderService
{
    private readonly IRepository<Order> _orderRepository;
    private readonly IUriComposer _uriComposer;
    private readonly HttpClient _client;
    private readonly IRepository<Basket> _basketRepository;
    private readonly IRepository<CatalogItem> _itemRepository;

    public OrderService(IRepository<Basket> basketRepository,
        IRepository<CatalogItem> itemRepository,
        IRepository<Order> orderRepository,
        IUriComposer uriComposer, HttpClient client)
    {
        _orderRepository = orderRepository;
        _uriComposer = uriComposer;
        _client = client;
        _basketRepository = basketRepository;
        _itemRepository = itemRepository;
    }

    public async Task CreateOrderAsync(int basketId, Address shippingAddress)
    {
        var basketSpec = new BasketWithItemsSpecification(basketId);
        var basket = await _basketRepository.GetBySpecAsync(basketSpec);

        Guard.Against.NullBasket(basketId, basket);
        Guard.Against.EmptyBasketOnCheckout(basket.Items);

        var catalogItemsSpecification = new CatalogItemsSpecification(basket.Items.Select(item => item.CatalogItemId).ToArray());
        var catalogItems = await _itemRepository.ListAsync(catalogItemsSpecification);

        var items = basket.Items.Select(basketItem =>
        {
            var catalogItem = catalogItems.First(c => c.Id == basketItem.CatalogItemId);
            var itemOrdered = new CatalogItemOrdered(catalogItem.Id, catalogItem.Name, _uriComposer.ComposePicUri(catalogItem.PictureUri));
            var orderItem = new OrderItem(itemOrdered, basketItem.UnitPrice, basketItem.Quantity);
            return orderItem;
        }).ToList();

        var order = new Order(basket.BuyerId, shippingAddress, items);
        var newOrder = new OrderDetails
        {
            ShippingAddress = $"{order.ShipToAddress.City} {order.ShipToAddress.State} {order.ShipToAddress.Street}",
            TotalPrice = order.Total(),
            OrderItems = order.OrderItems.Select(item => new OrderItemDetails { Name = item.ItemOrdered.ProductName, UnitPrice = item.UnitPrice })
        };
        var conn = "Endpoint=sb://e-shop-on-web.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=8hupd3EA7hlJYpVyuxbssnTf3ettwXRhs5JzuJ7s26k=";
        var queue = "inventoryqueue";
        var client = new QueueClient(conn, queue);
        await client.SendAsync(new Message(Encoding.UTF8.GetBytes(newOrder.ToJson())));
        await client.CloseAsync();
        await _client.PostAsJsonAsync<OrderDetails>("https://functionapp120220828212459.azurewebsites.net/api/Function1", newOrder);
        await _orderRepository.AddAsync(order);
    }
}
