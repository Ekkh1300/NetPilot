using System;
using System.Collections.Generic;
using System.Linq;
using NetPilot.Models;

namespace NetPilot.Services;

/// <summary>Persistent log of important network events (DNS changes, limits, blocks...).</summary>
public static class HistoryService
{
    private static readonly object _lock = new();
    private static List<HistoryEvent> _events;
    private const int MaxEvents = 2000;

    public static event Action Changed;

    public static List<HistoryEvent> GetAll()
    {
        lock (_lock)
        {
            _events ??= Storage.Load("history.json", new List<HistoryEvent>());
            return _events.OrderByDescending(e => e.Ts).ToList();
        }
    }

    public static void Add(string type, string title, string detail = "")
    {
        try
        {
            lock (_lock)
            {
                _events ??= Storage.Load("history.json", new List<HistoryEvent>());
                _events.Add(new HistoryEvent { Ts = DateTime.Now, Type = type, Title = title, Detail = detail });
                if (_events.Count > MaxEvents) _events.RemoveRange(0, _events.Count - MaxEvents);
                Storage.Save("history.json", _events);
            }
            Changed?.Invoke();
        }
        catch (Exception ex) { App.LogCrash(ex); }
    }

    public static void Clear()
    {
        lock (_lock)
        {
            _events = new List<HistoryEvent>();
            Storage.Save("history.json", _events);
        }
        Changed?.Invoke();
    }
}
