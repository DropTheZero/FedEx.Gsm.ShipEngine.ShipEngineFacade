// Decompiled with JetBrains decompiler
// Type: FedEx.Gsm.ShipEngine.ShipEngineFacade.ShipEngine
// Assembly: FedEx.Gsm.ShipEngine.ShipEngineFacade, Version=38.55.1083.0, Culture=neutral, PublicKeyToken=null
// MVID: 9EFD23F2-2AC9-4A0F-A69E-989CFB0B71BB
// Assembly location: C:\Users\elsmi\OneDrive\Desktop\work\fsm_modified\FedEx_modified\ShipManager\BIN\FedEx.Gsm.ShipEngine.ShipEngineFacade.dll

using eSRGApi;
using FedEx.Gsm.Common.Logging;
using FedEx.Gsm.Common.Parser.CTS;
using FedEx.Gsm.ShipEngine.DataAccess;
using FedEx.Gsm.ShipEngine.DataAccessService;
using FedEx.Gsm.ShipEngine.Entities;
using FedEx.Gsm.ShipEngine.Entities.RemotingSupport;
using FedEx.Gsm.ShipEngine.Entities.WebServiceRateReply;
using FedEx.Gsm.ShipEngine.Integration;
using FedEx.Gsm.ShipEngine.ServiceInterfaces;
using FedEx.Gsm.ShipEngine.Utilities.Common;
using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Sockets;
using System.ServiceModel;
using System.ServiceModel.Channels;
using System.Threading;

#nullable disable
namespace FedEx.Gsm.ShipEngine.ShipEngineFacade
{
  public class ShipEngine : 
    MarshalByRefObject,
    IShipEngineFacade,
    IShipService,
    IEditService,
    IDataAccess,
    IRate,
    IPickupLogic,
    ITrack,
    IRevenueService,
    IIntegration,
    ICommunication,
    ISmartPostService,
    INetworkClientServices,
    IFreightIntegration,
    IHazmatService,
    IHoldAtLocation,
    IRouteService,
    IShipEngineService
  {
    private ServiceHost _host;
    private static EventWaitHandle _ImplementRatesInProgressEventHandle = new EventWaitHandle(false, EventResetMode.ManualReset, "Global\\ImplementingRates");
    private static volatile FedEx.Gsm.ShipEngine.ShipEngineFacade.ShipEngine _shipEngine = (FedEx.Gsm.ShipEngine.ShipEngineFacade.ShipEngine) null;
    private static volatile bool _isCreateEngineAllowed = true;
    private static object syncLock = new object();
    private static object _callbackDelegateListLock = new object();
    private static List<NetworkClientEntry> _callbackIPAddressList = new List<NetworkClientEntry>();
    private const string _serviceURI = "RSS";
    private static object dbBackupSyncLock = new object();
    private static volatile bool _isAccountBackupInProgress = false;
    private static volatile bool _isShipmentInProgress = false;
    private static volatile bool _isFirstShipment = true;
    private static volatile bool _isLabelNeedInitiallization = true;
    private AutoResetEvent _PingThreadReadyEvent = new AutoResetEvent(false);

    private void BroadcastMessageToClients(object data)
    {
      try
      {
        BroadcastMessage broadcastMessage = data as BroadcastMessage;
        lock (FedEx.Gsm.ShipEngine.ShipEngineFacade.ShipEngine._callbackDelegateListLock)
        {
          foreach (NetworkClientEntry callbackIpAddress in FedEx.Gsm.ShipEngine.ShipEngineFacade.ShipEngine._callbackIPAddressList)
          {
            FxLogger.LogMessage(FedEx.Gsm.Common.Logging.LogLevel.Info, FxLogger.AppCode_Default, nameof (BroadcastMessageToClients), string.Format("Sending Unsolicited Message ({0}) to client at IP=[{1}]", (object) broadcastMessage?.Operation, (object) callbackIpAddress.IPAddress));
            new FedEx.Gsm.ShipEngine.Entities.NetworkUdpClient<BroadcastMessage>(callbackIpAddress.IPAddress, 5120).send(broadcastMessage);
          }
        }
      }
      catch (Exception ex)
      {
        FxLogger.LogMessage(FedEx.Gsm.Common.Logging.LogLevel.Error, FxLogger.AppCode_Default, nameof (BroadcastMessageToClients), "Caught Exception in BroadcastMessageToClients e.message=" + ex.Message + "See dumpfile for callstack");
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
      }
    }

    public void PublishMessage(BroadcastMessage message, bool waitForCompletion)
    {
      try
      {
        FxLogger.LogMessage(FedEx.Gsm.Common.Logging.LogLevel.Info, FxLogger.AppCode_Default, nameof (PublishMessage), "Starting message broadcast for " + message.Operation.ToString());
        Thread thread = new Thread(new ParameterizedThreadStart(this.BroadcastMessageToClients));
        thread.Start((object) message);
        if (!waitForCompletion)
          return;
        thread.Join();
      }
      catch (Exception ex)
      {
        FxLogger.LogMessage(FedEx.Gsm.Common.Logging.LogLevel.Error, FxLogger.AppCode_Default, nameof (PublishMessage), "Caught Exception in PublishMessage e.message=" + ex.Message + "See dumpfile for callstack");
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
      }
    }

    public ServiceResponse UpdateAccountMeterInClientTable(NetworkClientEntry entry)
    {
      ServiceResponse serviceResponse = new ServiceResponse();
      lock (FedEx.Gsm.ShipEngine.ShipEngineFacade.ShipEngine._callbackDelegateListLock)
      {
        NetworkClientEntry networkClientEntry = FedEx.Gsm.ShipEngine.ShipEngineFacade.ShipEngine._callbackIPAddressList.Find((Predicate<NetworkClientEntry>) (e => e == entry));
        if (networkClientEntry != (NetworkClientEntry) null)
        {
          networkClientEntry.AccountNumber = entry.AccountNumber;
          networkClientEntry.MeterNumber = entry.MeterNumber;
        }
      }
      return serviceResponse;
    }

    public ServiceResponse RegisterForUnsolicitedServerMessages(NetworkClientEntry entry)
    {
      ServiceResponse serviceResponse = new ServiceResponse();
      lock (FedEx.Gsm.ShipEngine.ShipEngineFacade.ShipEngine._callbackDelegateListLock)
      {
        if (FedEx.Gsm.ShipEngine.ShipEngineFacade.ShipEngine._callbackIPAddressList.Contains(entry))
        {
          FxLogger.LogMessage(FedEx.Gsm.Common.Logging.LogLevel.Info, FxLogger.AppCode_Default, "AddCallbackEvent", "Callback at IP Address [" + entry.IPAddress + "] is already Registered.");
          return serviceResponse;
        }
        try
        {
          if (FDXChannel.NeedsRemoteConnections())
          {
            if (OperationContext.Current.IncomingMessageProperties[RemoteEndpointMessageProperty.Name] is RemoteEndpointMessageProperty incomingMessageProperty)
            {
              string address = incomingMessageProperty.Address;
              entry.IPAddress = address;
              FedEx.Gsm.ShipEngine.ShipEngineFacade.ShipEngine._callbackIPAddressList.Add(entry);
              FxLogger.LogMessage(FedEx.Gsm.Common.Logging.LogLevel.Info, FxLogger.AppCode_Default, "AddCallbackEvent", string.Format("Callback for client {0} at IP Address [{1}] added.", (object) entry.NetworkClientID, (object) address));
            }
            else
              FxLogger.LogMessage(FedEx.Gsm.Common.Logging.LogLevel.Warning, FxLogger.AppCode_Default, "AddCallbackEvent", "Failed to get address for client " + entry.NetworkClientID.ToString());
          }
          else
          {
            entry.IPAddress = "127.0.0.1";
            FedEx.Gsm.ShipEngine.ShipEngineFacade.ShipEngine._callbackIPAddressList.Add(entry);
            FxLogger.LogMessage(FedEx.Gsm.Common.Logging.LogLevel.Info, FxLogger.AppCode_Default, "AddCallbackEvent", "Callback for localhost added.");
          }
        }
        catch (Exception ex)
        {
          FxLogger.LogMessage(FedEx.Gsm.Common.Logging.LogLevel.Warning, FxLogger.AppCode_Default, "ShipEngine.RegisterForUnsolicitedServerMessages", "Failed to get address " + ex?.ToString());
        }
      }
      return serviceResponse;
    }

    public ServiceResponse UnRegisterForUnsolicitedServerMessages(int NetworkClientID)
    {
      ServiceResponse serviceResponse = new ServiceResponse();
      lock (FedEx.Gsm.ShipEngine.ShipEngineFacade.ShipEngine._callbackDelegateListLock)
      {
        NetworkClientEntry networkClientEntry = new NetworkClientEntry(NetworkClientID, string.Empty, string.Empty, string.Empty);
        if (FedEx.Gsm.ShipEngine.ShipEngineFacade.ShipEngine._callbackIPAddressList.Contains(networkClientEntry))
        {
          FxLogger.LogMessage(FedEx.Gsm.Common.Logging.LogLevel.Info, FxLogger.AppCode_Default, "AddCallbackEvent", string.Format("Removing Network Client {0} From Registration List.", (object) NetworkClientID));
          FedEx.Gsm.ShipEngine.ShipEngineFacade.ShipEngine._callbackIPAddressList.Remove(networkClientEntry);
          return serviceResponse;
        }
      }
      return serviceResponse;
    }

    public ServiceResponse GetConnectedClientList(
      out List<NetworkClientEntry> clientList,
      out List<NetworkClientEntry> unresponsiveClients)
    {
      int index = 0;
      unresponsiveClients = new List<NetworkClientEntry>();
      clientList = new List<NetworkClientEntry>();
      lock (FedEx.Gsm.ShipEngine.ShipEngineFacade.ShipEngine._callbackDelegateListLock)
      {
        for (; index < FedEx.Gsm.ShipEngine.ShipEngineFacade.ShipEngine._callbackIPAddressList.Count; ++index)
        {
          if (this.PingNetworkClient(FedEx.Gsm.ShipEngine.ShipEngineFacade.ShipEngine._callbackIPAddressList[index].NetworkClientID).HasError)
            unresponsiveClients.Add(FedEx.Gsm.ShipEngine.ShipEngineFacade.ShipEngine._callbackIPAddressList[index]);
          else
            clientList.Add(FedEx.Gsm.ShipEngine.ShipEngineFacade.ShipEngine._callbackIPAddressList[index]);
        }
      }
      return new ServiceResponse();
    }

    public ServiceResponse CloseNetworkClient(int NetworkClientID)
    {
      FxLogger.LogMessage(FedEx.Gsm.Common.Logging.LogLevel.Info, FxLogger.AppCode_Default, nameof (CloseNetworkClient), "Sending Close Request to Network Client ID " + NetworkClientID.ToString());
      try
      {
        BroadcastMessage broadcastMessage = new BroadcastMessage(OperationType.ShutdownClient, string.Empty);
        NetworkClientEntry filter = new NetworkClientEntry();
        filter.NetworkClientID = NetworkClientID;
        NetworkClientEntry networkClientEntry = FedEx.Gsm.ShipEngine.ShipEngineFacade.ShipEngine._callbackIPAddressList.Find((Predicate<NetworkClientEntry>) (e => e == filter));
        if (filter != (NetworkClientEntry) null)
          new FedEx.Gsm.ShipEngine.Entities.NetworkUdpClient<BroadcastMessage>(networkClientEntry.IPAddress, 5120).send(broadcastMessage);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
      }
      return new ServiceResponse();
    }

    public ServiceResponse SendNetworkClientMessage(string ipAddress, string message)
    {
      try
      {
        if (message == null)
          message = string.Empty;
        BroadcastMessage broadcastMessage = new BroadcastMessage(OperationType.DisplayMessage, message);
        new FedEx.Gsm.ShipEngine.Entities.NetworkUdpClient<BroadcastMessage>(ipAddress, 5120).send(broadcastMessage);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
      }
      return new ServiceResponse();
    }

    public ServiceResponse SendNetworkClientMessage(int networkClientID, BroadcastMessage message)
    {
      FxLogger.LogMessage(FedEx.Gsm.Common.Logging.LogLevel.Info, FxLogger.AppCode_Default, nameof (SendNetworkClientMessage), "Sending message " + message.ToString() + " to Network Client ID " + networkClientID.ToString());
      if (message == null)
      {
        FxLogger.LogMessage(FedEx.Gsm.Common.Logging.LogLevel.Info, FxLogger.AppCode_Default, nameof (SendNetworkClientMessage), "message payload is null. Message to network client ID " + networkClientID.ToString() + " failed.");
        return new ServiceResponse(0);
      }
      try
      {
        NetworkClientEntry filter = new NetworkClientEntry();
        filter.NetworkClientID = networkClientID;
        NetworkClientEntry networkClientEntry = FedEx.Gsm.ShipEngine.ShipEngineFacade.ShipEngine._callbackIPAddressList.Find((Predicate<NetworkClientEntry>) (e => e == filter));
        if (filter != (NetworkClientEntry) null)
          new FedEx.Gsm.ShipEngine.Entities.NetworkUdpClient<BroadcastMessage>(networkClientEntry.IPAddress, 5120).send(message);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
      }
      return new ServiceResponse();
    }

    private void PingResponseThread(string ipAddress, out bool isResponsive)
    {
      isResponsive = false;
      bool flag = false;
      FedEx.Gsm.ShipEngine.Entities.NetworkUDPServer<BroadcastMessage> networkUdpServer = new FedEx.Gsm.ShipEngine.Entities.NetworkUDPServer<BroadcastMessage>(5122);
      networkUdpServer.SetSocketReceiveTimeout(3);
      object obj = (object) null;
      try
      {
        this._PingThreadReadyEvent.Set();
        obj = (object) networkUdpServer.receive();
      }
      catch (SocketException ex)
      {
        flag = true;
      }
      if (!flag && obj is BroadcastMessage && ((BroadcastMessage) obj).Operation == OperationType.Ping && ((BroadcastMessage) obj).Message.Equals(ipAddress))
        isResponsive = true;
      networkUdpServer.stop();
    }

    public ServiceResponse PingNetworkClient(int NetworkClientID)
    {
      bool responseReceived = false;
      FxLogger.LogMessage(FedEx.Gsm.Common.Logging.LogLevel.Info, FxLogger.AppCode_Default, nameof (PingNetworkClient), "Sending Ping Request to Network Client ID " + NetworkClientID.ToString());
      try
      {
        NetworkClientEntry filter = new NetworkClientEntry();
        filter.NetworkClientID = NetworkClientID;
        NetworkClientEntry foundEntry = FedEx.Gsm.ShipEngine.ShipEngineFacade.ShipEngine._callbackIPAddressList.Find((Predicate<NetworkClientEntry>) (e => e == filter));
        if (filter != (NetworkClientEntry) null)
        {
          BroadcastMessage broadcastMessage = new BroadcastMessage(OperationType.Ping, foundEntry.IPAddress);
          Thread thread = new Thread((ThreadStart) (() => this.PingResponseThread(foundEntry.IPAddress, out responseReceived)));
          thread.Start();
          this._PingThreadReadyEvent.WaitOne();
          new FedEx.Gsm.ShipEngine.Entities.NetworkUdpClient<BroadcastMessage>(foundEntry.IPAddress, 5120).send(broadcastMessage);
          thread.Join();
        }
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
      }
      return responseReceived ? new ServiceResponse() : new ServiceResponse(new Error(-1));
    }

    public ServiceResponse GetFastPojoPort(out string fastPojoPort)
    {
      ServiceResponse fastPojoPort1 = new ServiceResponse();
      fastPojoPort = new FedEx.Gsm.Common.ConfigManager.ConfigManager().FastPojoPort;
      return fastPojoPort1;
    }

    public static FedEx.Gsm.ShipEngine.ShipEngineFacade.ShipEngine GetShipEngine()
    {
      if (FedEx.Gsm.ShipEngine.ShipEngineFacade.ShipEngine._shipEngine == null)
      {
        lock (FedEx.Gsm.ShipEngine.ShipEngineFacade.ShipEngine.syncLock)
        {
          if (FedEx.Gsm.ShipEngine.ShipEngineFacade.ShipEngine._shipEngine == null)
            FedEx.Gsm.ShipEngine.ShipEngineFacade.ShipEngine._shipEngine = new FedEx.Gsm.ShipEngine.ShipEngineFacade.ShipEngine();
        }
      }
      return FedEx.Gsm.ShipEngine.ShipEngineFacade.ShipEngine._shipEngine;
    }

    public static void DisposeShipEngine(bool skipAdmin)
    {
      if (FedEx.Gsm.ShipEngine.ShipEngineFacade.ShipEngine._shipEngine == null)
        return;
      FedEx.Gsm.ShipEngine.ShipEngineFacade.ShipEngine._shipEngine = (FedEx.Gsm.ShipEngine.ShipEngineFacade.ShipEngine) null;
      GC.Collect();
      FedEx.Gsm.ShipEngine.ShipEngineFacade.ShipEngine._isCreateEngineAllowed = false;
    }

    public static void AllowShipEngineToBeCreated() => FedEx.Gsm.ShipEngine.ShipEngineFacade.ShipEngine._isCreateEngineAllowed = true;

    public override object InitializeLifetimeService() => (object) null;

    public bool StartRemotingServer()
    {
      string inLocation = nameof (StartRemotingServer);
      FxLogger.LogMessage(FedEx.Gsm.Common.Logging.LogLevel.Info, FxLogger.AppCode_Default, inLocation, "Entering Function");
      bool flag = true;
      FedEx.Gsm.ShipEngine.ShipEngineFacade.ShipEngine shipEngine = FedEx.Gsm.ShipEngine.ShipEngineFacade.ShipEngine.GetShipEngine();
      try
      {
        int num = FDXChannel.NeedsRemoteConnections() ? 1 : 0;
        FedEx.Gsm.Common.ConfigManager.ConfigManager configManager = new FedEx.Gsm.Common.ConfigManager.ConfigManager(FedEx.Gsm.Common.ConfigManager.ConfigManager.Sections.SETTINGS);
        Binding binding;
        string uriString;
        if (num != 0)
        {
          string empty = string.Empty;
          configManager.GetProfileString("NetworkClient", "ServerPort", out empty, empty);
          binding = (Binding) new NetTcpBinding(SecurityMode.None)
          {
            MaxReceivedMessageSize = (long) int.MaxValue
          };
          uriString = "net.tcp://0.0.0.0:" + empty + "/RSS";
        }
        else
        {
          binding = (Binding) new NetNamedPipeBinding()
          {
            MaxReceivedMessageSize = (long) int.MaxValue
          };
          uriString = "net.pipe://localhost/FdxShipEnginePipe/RSS";
        }
        long lval;
        if (!configManager.GetProfileLong("NetworkClient", "SERVERRESPONSETIMEOUT", out lval))
          lval = 3600000L;
        binding.ReceiveTimeout = TimeSpan.FromMilliseconds((double) lval);
        Uri address = new Uri(uriString);
        this._host = new ServiceHost((object) shipEngine, new Uri[1]
        {
          address
        });
        ServiceBehaviorAttribute behaviorAttribute = this._host.Description.Behaviors.Find<ServiceBehaviorAttribute>();
        behaviorAttribute.InstanceContextMode = InstanceContextMode.Single;
        behaviorAttribute.ConcurrencyMode = ConcurrencyMode.Multiple;
        this._host.AddServiceEndpoint(typeof (IShipEngineService), binding, address);
        this._host.Faulted += new EventHandler(this._host_Faulted);
        this._host.Open();
        FxLogger.LogMessage(FedEx.Gsm.Common.Logging.LogLevel.Info, FxLogger.AppCode_Default, inLocation, "Started Listener");
      }
      catch (Exception ex)
      {
        Debugger.Log(1, "Info", "Channel Started? Not!");
        flag = false;
        FxLogger.LogMessage(FedEx.Gsm.Common.Logging.LogLevel.Error, FxLogger.AppCode_Default, inLocation, "Caught an exception setting up the remoting channels: " + ex.Message);
      }
      return flag;
    }

    private void _host_Faulted(object sender, EventArgs e)
    {
      FxLogger.LogMessage(FedEx.Gsm.Common.Logging.LogLevel.Error, FxLogger.AppCode_Default, "HostFaulted", "ServiceHost faulted.");
    }

    public int InitializeSmartPost()
    {
      return ShipEngineComponents.SmartPostComponent.InitializeSmartPost();
    }

    public ServiceResponse LookupSmartPostLabelFormSetting(
      string accountNumber,
      string meterNumber,
      out FormSetting formSetting)
    {
      formSetting = (FormSetting) null;
      return ShipEngineComponents.SmartPostComponent.LookupSmartPostLabelFormSetting(accountNumber, meterNumber, out formSetting);
    }

    public ServiceResponse IsSPValidCurrentSender(SenderRequest senderRequest)
    {
      return ShipEngineComponents.SmartPostComponent.IsSPValidCurrentSender(senderRequest);
    }

    public SenderResponse GetSmartPostReturnSender(SenderRequest senderRequest)
    {
      return ShipEngineComponents.SmartPostComponent.GetSmartPostReturnSender(senderRequest);
    }

    public ServiceResponse DownloadSmartPostReturnTrackingNumberRange(SenderRequest senderRequest)
    {
      return ShipEngineComponents.SmartPostComponent.DownloadSmartPostReturnTrackingNumberRange(senderRequest);
    }

    public ServiceResponse TestShipment(Shipment shipment)
    {
      try
      {
        if (ShipEngineCommonUtilities.IsEngineOnly())
        {
          Error error = new Error();
          Account account = ShipEngineComponents.DataAccessService.GetAccount(shipment.Account.AccountNumber, shipment.Account.MeterNumber, error);
          if (account != null)
            shipment.Account = new Account(account);
        }
        return ShipEngineComponents.ShipEngine.TestShipment(shipment);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse TestShipment(Shipment shipment, bool validateAllPackages)
    {
      try
      {
        if (ShipEngineCommonUtilities.IsEngineOnly())
        {
          Error error = new Error();
          Account account = ShipEngineComponents.DataAccessService.GetAccount(shipment.Account.AccountNumber, shipment.Account.MeterNumber, error);
          if (account != null)
            shipment.Account = new Account(account);
        }
        return ShipEngineComponents.ShipEngine.TestShipment(shipment, validateAllPackages);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ShipmentLabelResponse ProcessShipment(Shipment shipment)
    {
      try
      {
        if (FedEx.Gsm.ShipEngine.ShipEngineFacade.ShipEngine._ImplementRatesInProgressEventHandle.WaitOne(0, true))
        {
          FxLogger.LogMessage(FedEx.Gsm.Common.Logging.LogLevel.Error, FxLogger.AppCode_RateSvc, nameof (ProcessShipment), "Rating Initialization In Progress");
          return new ShipmentLabelResponse(new Error(6666, "Rating Initialization Is In Progress"));
        }
        if (!FedEx.Gsm.ShipEngine.ShipEngineFacade.ShipEngine._isCreateEngineAllowed)
          return new ShipmentLabelResponse(new Error()
          {
            Code = 8001,
            Message = "Restore GSMAccountData is in progress, come back later"
          });
        if (ShipEngineCommonUtilities.IsEngineOnly())
        {
          Error error = new Error();
          Account account = ShipEngineComponents.DataAccessService.GetAccount(shipment.Account.AccountNumber, shipment.Account.MeterNumber, error);
          if (account != null)
            shipment.Account = new Account(account);
        }
        return ShipEngineComponents.ShipEngine.ProcessShipment(shipment);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ShipmentLabelResponse(new Error(4));
      }
    }

    public ShipmentLabelResponse ProcessMailroomLabel(Shipment shipment, string tmpMailTrkNo)
    {
      if (FedEx.Gsm.ShipEngine.ShipEngineFacade.ShipEngine._isCreateEngineAllowed)
        return ShipEngineComponents.ShipEngine.ProcessMailroomLabel(shipment, tmpMailTrkNo);
      return new ShipmentLabelResponse(new Error()
      {
        Code = 8001,
        Message = "Restore GSMAccountData is in progress, come back later"
      });
    }

    public ShipmentLabelResponse ProcessShipment(Shipment shipment, DocTab doctab)
    {
      try
      {
        if (FedEx.Gsm.ShipEngine.ShipEngineFacade.ShipEngine._ImplementRatesInProgressEventHandle.WaitOne(0, true))
        {
          FxLogger.LogMessage(FedEx.Gsm.Common.Logging.LogLevel.Error, FxLogger.AppCode_RateSvc, nameof (ProcessShipment), "Rating Initialization In Progress");
          return new ShipmentLabelResponse(new Error(6666, "Rating Initialization Is In Progress"));
        }
        if (doctab == null)
          doctab = new DocTab();
        if (!FedEx.Gsm.ShipEngine.ShipEngineFacade.ShipEngine._isCreateEngineAllowed)
          return new ShipmentLabelResponse(new Error()
          {
            Code = 8001,
            Message = "Restore GSMAccountData is in progress, come back later"
          });
        if (ShipEngineCommonUtilities.IsEngineOnly())
        {
          Error error = new Error();
          Account account = ShipEngineComponents.DataAccessService.GetAccount(shipment.Account.AccountNumber, shipment.Account.MeterNumber, error);
          if (account != null)
            shipment.Account = new Account(account);
        }
        return ShipEngineComponents.ShipEngine.ProcessShipment(shipment, doctab);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ShipmentLabelResponse(new Error(4));
      }
    }

    public ShipmentLabelResponse ProcessShipment(ShipmentRequest shipmentRequest)
    {
      try
      {
        if (FedEx.Gsm.ShipEngine.ShipEngineFacade.ShipEngine._ImplementRatesInProgressEventHandle.WaitOne(0, true))
        {
          FxLogger.LogMessage(FedEx.Gsm.Common.Logging.LogLevel.Error, FxLogger.AppCode_RateSvc, nameof (ProcessShipment), "Rating Initialization In Progress");
          return new ShipmentLabelResponse(new Error(6666, "Rating Initialization Is In Progress"));
        }
        if (shipmentRequest.DocTabOptions == null)
          shipmentRequest.DocTabOptions = new DocTab();
        if (!FedEx.Gsm.ShipEngine.ShipEngineFacade.ShipEngine._isCreateEngineAllowed)
          return new ShipmentLabelResponse(new Error()
          {
            Code = 8001,
            Message = "Restore GSMAccountData is in progress, come back later"
          });
        ShipmentLabelResponse shipmentLabelResponse1 = new ShipmentLabelResponse();
        if (!FedEx.Gsm.ShipEngine.ShipEngineFacade.ShipEngine._isAccountBackupInProgress)
        {
          FedEx.Gsm.ShipEngine.ShipEngineFacade.ShipEngine._isShipmentInProgress = true;
          if (ShipEngineCommonUtilities.IsEngineOnly())
          {
            Error error = new Error();
            Account account = ShipEngineComponents.DataAccessService.GetAccount(shipmentRequest.Shipment.Account.AccountNumber, shipmentRequest.Shipment.Account.MeterNumber, error);
            account.Address = shipmentRequest.Shipment.Account.Address;
            account.OriginId = shipmentRequest.Shipment.Account.OriginId;
            account.ADRReferenceNbr = shipmentRequest.Shipment.Account.ADRReferenceNbr;
            if (account != null)
              shipmentRequest.Shipment.Account = new Account(account);
          }
          ShipmentLabelResponse shipmentLabelResponse2 = shipmentRequest.Shipment.Carrier != Shipment.CarrierType.SmartPost ? ShipEngineComponents.ShipEngine.ProcessShipment(shipmentRequest) : this.ProcessSmartPostShipment(shipmentRequest);
          FedEx.Gsm.ShipEngine.ShipEngineFacade.ShipEngine._isShipmentInProgress = false;
          bool flag = (shipmentRequest.Shipment.Carrier == Shipment.CarrierType.Ground || shipmentRequest.Shipment.Carrier == Shipment.CarrierType.SmartPost) && FedEx.Gsm.ShipEngine.ShipEngineFacade.ShipEngine._isLabelNeedInitiallization;
          if (FedEx.Gsm.ShipEngine.ShipEngineFacade.ShipEngine._isFirstShipment | flag)
          {
            try
            {
              Process.GetCurrentProcess().MaxWorkingSet = (IntPtr) ((int) Process.GetCurrentProcess().MaxWorkingSet - 1);
            }
            catch (Exception ex)
            {
              FxLogger.LogMessage(FedEx.Gsm.Common.Logging.LogLevel.Info, FxLogger.AppCode_Default, "WorkingSetStatus", "Set MaxWorkingSet throw an exception");
            }
            FedEx.Gsm.ShipEngine.ShipEngineFacade.ShipEngine._isFirstShipment = false;
            if (flag)
              FedEx.Gsm.ShipEngine.ShipEngineFacade.ShipEngine._isLabelNeedInitiallization = false;
          }
          return shipmentLabelResponse2;
        }
        return new ShipmentLabelResponse(new Error()
        {
          Code = 8001,
          Message = "Backup of GSMAccount Data in progress, come back later."
        });
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ShipmentLabelResponse(new Error(4));
      }
    }

    private ShipmentLabelResponse ProcessSmartPostShipment(ShipmentRequest shipmentRequest)
    {
      return ShipEngineComponents.SmartPostComponent.ProcessSmartPostShipment(shipmentRequest);
    }

    public ShipmentLabelResponse EndMPSShipment(
      ShipmentLabelResponse shipmentLabelResponse,
      Shipment.RateChosen rateChosen)
    {
      try
      {
        return ShipEngineComponents.ShipEngine.EndMPSShipment(shipmentLabelResponse, rateChosen);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ShipmentLabelResponse(new Error(4));
      }
    }

    public ShipmentLabelResponse EndMPSShipment(
      ShipmentLabelResponse shipmentLabelResponse,
      Shipment.RateChosen rateChosen,
      DocTab doctab)
    {
      try
      {
        return ShipEngineComponents.ShipEngine.EndMPSShipment(shipmentLabelResponse, rateChosen, doctab);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ShipmentLabelResponse(new Error(4));
      }
    }

    public ShipmentLabelResponse EndMpsShipment(
      ShipmentLabelResponse shipmentLabelResponse,
      Shipment.RateChosen rateChosen,
      DocTab doctab,
      DocTab totalDocTab,
      bool printTotalDocTab)
    {
      try
      {
        return ShipEngineComponents.ShipEngine.EndMpsShipment(shipmentLabelResponse, rateChosen, doctab, totalDocTab, printTotalDocTab);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ShipmentLabelResponse(new Error(4));
      }
    }

    public ShipmentLabelResponse EndShipment(
      ShipmentLabelResponse shipmentLabelResponse,
      Shipment.RateChosen rateChosen,
      DocTab doctab)
    {
      try
      {
        return ShipEngineComponents.ShipEngine.EndShipment(shipmentLabelResponse, rateChosen, doctab);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ShipmentLabelResponse(new Error(4));
      }
    }

    public CLSLabelResponse GetLabelBuffer(Shipment shipment)
    {
      try
      {
        return ShipEngineComponents.ShipEngine.GetLabelBuffer(shipment);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new CLSLabelResponse(new Error(4));
      }
    }

    public ShipmentLabelResponse GetReprintLabels(ShipmentRequest shipmentFilter)
    {
      try
      {
        return ShipEngineComponents.ShipEngine.GetReprintLabels(shipmentFilter);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ShipmentLabelResponse(new Error(4));
      }
    }

    public ShipmentLabelResponse PrintTestLabel(ShipmentRequest shipmentFilter)
    {
      try
      {
        return ShipEngineComponents.ShipEngine.PrintTestLabel(shipmentFilter);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ShipmentLabelResponse(new Error(4));
      }
    }

    public PolicyResponse GetIGDDAccountEnablementInfo(Shipment shipment)
    {
      try
      {
        return ShipEngineComponents.ShipEngine.GetIGDDAccountEnablementInfo(shipment);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new PolicyResponse();
      }
    }

    public TrackingNumberInfoResponse GetNextTrackingNumber(
      TrackingNumberInfoAccess trackingNumberIn)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.GetNextTrackingNumber(trackingNumberIn);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new TrackingNumberInfoResponse(new Error(4));
      }
    }

    public TrackingNumberInfoResponse GetNextTrackingNumber(
      TrackingNumberInfoAccess trackingNumberIn,
      int nbrOfTrackingNumber)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.GetNextTrackingNumber(trackingNumberIn, nbrOfTrackingNumber);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new TrackingNumberInfoResponse(new Error(4));
      }
    }

    public TrackingNumberInfoResponse GetNextTrackingNumber(
      TrackingNumberInfoAccess trackingNumberIn,
      short trackingNumberType)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.GetNextTrackingNumber(trackingNumberIn, trackingNumberType);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new TrackingNumberInfoResponse(new Error(4));
      }
    }

    public TrackingNumberInfoResponse GetNextTrackingNumber(Shipment shipment)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.GetNextTrackingNumber(shipment);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new TrackingNumberInfoResponse(new Error(4));
      }
    }

    public TrackingNumberInfoResponse IncrementTrackingNumber(TrackingNumberInfoAccess filter)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.IncrementTrackingNumber(filter);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new TrackingNumberInfoResponse(new Error(4));
      }
    }

    public DataTableResponse GetStateProvinceList(string country)
    {
      try
      {
        return ShipEngineCommonUtilities.IsESRGCountry(country) ? ShipEngineComponents.RouteService.GetStateProvinceList(country) : ShipEngineComponents.DataAccessService.GetStateProvinceList(country);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new DataTableResponse(new DataTable(), new Error(4));
      }
    }

    public MasterShipmentResponse CreateTDMasterShipment(
      TDMasterShipmentRequest masterShipmentRequest)
    {
      try
      {
        return ShipEngineComponents.ShipEngine.CreateTDMasterShipment(masterShipmentRequest);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new MasterShipmentResponse(new Error(4));
      }
    }

    public ServiceResponse DeleteTDMasterShipment(MasterShipment masterShipment)
    {
      try
      {
        return ShipEngineComponents.ShipEngine.DeleteTDMasterShipment(masterShipment);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ShipmentLabelResponse ModifyTDMasterShipment(MasterShipment masterShipment)
    {
      try
      {
        return ShipEngineComponents.ShipEngine.ModifyTDMasterShipment(masterShipment);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ShipmentLabelResponse(new Error(4));
      }
    }

    public ShipmentLabelResponse CompleteTDShipment(MasterShipment masterShipment)
    {
      try
      {
        return ShipEngineComponents.ShipEngine.CompleteTDShipment(masterShipment);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ShipmentLabelResponse(new Error(4));
      }
    }

    public ShipmentResponse CreateIPDMaster(Shipment shipment)
    {
      try
      {
        return ShipEngineComponents.ShipEngine.CreateIPDMaster(shipment);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ShipmentResponse(new Error(4));
      }
    }

    public ShipmentLabelResponse ModifyCRN(Shipment shipment)
    {
      return ShipEngineComponents.ShipEngine.ModifyCRN(shipment);
    }

    public TradelinkCIResponse GetTradelinkCI(
      string accountNumber,
      string meterNumber,
      string masterTrackingNumber)
    {
      return ShipEngineComponents.ShipEngine.GetTradelinkCI(accountNumber, meterNumber, masterTrackingNumber);
    }

    public CreateDeviceParingTokenServiceResponse AlternateDeviceRegistration(
      DeviceRegistrationServiceRequest deviceRegistrationServiceRequest)
    {
      try
      {
        return ShipEngineComponents.APIService.AlternateDeviceRegistration(deviceRegistrationServiceRequest);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        CreateDeviceParingTokenServiceResponse tokenServiceResponse = new CreateDeviceParingTokenServiceResponse();
        tokenServiceResponse.Error = new Error(4);
        return tokenServiceResponse;
      }
    }

    public ServiceResponse PortalDeviceRegistration(
      ActivateDeviceServiceRequest activateDeviceServiceRequest)
    {
      try
      {
        return ShipEngineComponents.APIService.PortalDeviceRegistration(activateDeviceServiceRequest);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse()
        {
          Error = new Error(4)
        };
      }
    }

    public GroundTerminalInfo GetTerminalInfo(Shipment shipment, Error error)
    {
      return ShipEngineComponents.RouteService.GetTerminalInfo(shipment, error);
    }

    public PostalCodeLookupResponse PostalCodeLookup(PostalCodeLookupRequest postalCodeLookupRequest)
    {
      return ShipEngineComponents.RouteService.PostalCodeLookup(postalCodeLookupRequest);
    }

    public ShipmentLabelResponse ConfirmIPDMaster(Shipment shipment)
    {
      try
      {
        return ShipEngineComponents.ShipEngine.ConfirmIPDMaster(shipment);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ShipmentLabelResponse(new Error(4));
      }
    }

    public ServiceResponse IPDMasterAlreadyExists(Shipment shipment)
    {
      try
      {
        return ShipEngineComponents.ShipEngine.IPDMasterAlreadyExists(shipment);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ShipmentLabelResponse ConfirmIPDMaster(IPDMasterShipmentRequest ipdMasterShipmentRequest)
    {
      try
      {
        return ShipEngineComponents.ShipEngine.ConfirmIPDMaster(ipdMasterShipmentRequest);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ShipmentLabelResponse(new Error(4));
      }
    }

    public ShipmentResponse ConsolidateIPDShipments(
      string mawbAccountNumber,
      string mawbMeterNumber,
      string mawbNumber)
    {
      try
      {
        return ShipEngineComponents.ShipEngine.ConsolidateIPDShipments(mawbAccountNumber, mawbMeterNumber, mawbNumber);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ShipmentResponse(new Error(4));
      }
    }

    public ServiceResponse ModifyIPDMaster(Shipment shipment)
    {
      return ShipEngineComponents.ShipEngine.ModifyIPDMaster(shipment);
    }

    public ShipmentLabelResponse GetReprintIPDLabels(Shipment shipmentFilter)
    {
      try
      {
        return ShipEngineComponents.ShipEngine.GetReprintIPDLabels(shipmentFilter);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ShipmentLabelResponse(new Error(4));
      }
    }

    public ServiceResponse DeleteIPDMaster(Shipment shipment)
    {
      try
      {
        return ShipEngineComponents.ShipEngine.DeleteIPDMaster(shipment);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public List<ShipmentResponse> GetListOfOpenIPDMasters()
    {
      try
      {
        return ShipEngineComponents.ShipEngine.GetListOfOpenIPDMasters();
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new List<ShipmentResponse>();
      }
    }

    public List<ShipmentResponse> GetListOfOpenIPDMasters(string meterNumber, string accountNumber)
    {
      try
      {
        return ShipEngineComponents.ShipEngine.GetListOfOpenIPDMasters(meterNumber, accountNumber);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new List<ShipmentResponse>();
      }
    }

    [Obsolete("Use SvcListResponse GetExpressServiceList(ExpressServiceType expSvcType) instead.")]
    public SvcListResponse GetInternationalExpressServiceList()
    {
      try
      {
        return ShipEngineComponents.ShipEngine.GetInternationalExpressServiceList();
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new SvcListResponse(new SvcList(), new Error(4));
      }
    }

    public SvcListResponse GetExpressServiceList(ExpressServiceType expSvcType)
    {
      try
      {
        return ShipEngineComponents.ShipEngine.GetExpressServiceList(expSvcType);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new SvcListResponse(new SvcList(), new Error(4));
      }
    }

    public List<Pkging> GetAllPackageTypeList()
    {
      try
      {
        return ShipEngineComponents.ShipEngine.GetAllPackageTypeList();
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new List<Pkging>();
      }
    }

    public List<Pkging> GetAllPackageTypeList(Account account)
    {
      try
      {
        return ShipEngineComponents.ShipEngine.GetAllPackageTypeList(account);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new List<Pkging>();
      }
    }

    public ShipmentLabelResponse PrintRevenueFloppyLabel(Account account)
    {
      try
      {
        return ShipEngineComponents.ShipEngine.PrintRevenueFloppyLabel(account);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ShipmentLabelResponse(new Error(4));
      }
    }

    public ShipmentResponse UploadETDFiles(Shipment shipment, bool bUploadNow)
    {
      try
      {
        return ShipEngineComponents.ShipEngine.UploadETDFiles(shipment, bUploadNow);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ShipmentResponse();
      }
    }

    public ServiceResponse UpdateETDUploadStatus(
      string trackingNumber,
      string meterNumber,
      string accountNumber,
      ETD_UploadStatus status)
    {
      try
      {
        return ShipEngineComponents.ShipEngine.UpdateETDUploadStatus(trackingNumber, meterNumber, accountNumber, status);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(3));
      }
    }

    public ShipmentResponse ProcessExpressTag(ShipmentRequest shipment)
    {
      try
      {
        return ShipEngineComponents.ShipEngine.ProcessExpressTag(shipment);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ShipmentResponse();
      }
    }

    public ShipmentResponse ProcessExpressDeleteTag(ShipmentRequest shipment)
    {
      try
      {
        return ShipEngineComponents.ShipEngine.ProcessExpressDeleteTag(shipment);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ShipmentResponse();
      }
    }

    public int Retrieve<Tobject>(Tobject filter, out Tobject output, out Error error) where Tobject : class, new()
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Retrieve<Tobject>(filter, out output, out error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        output = default (Tobject);
        error = new Error(4);
        return 4;
      }
    }

    public int Retrieve(object filter, out object output, out Error error)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Retrieve<object>(filter, out output, out error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        output = (object) null;
        error = new Error(4);
        return 4;
      }
    }

    public int Modify(object input, Error error)
    {
      try
      {
        return input is MasterShipment ? ShipEngineComponents.ShipEngine.ModifyMasterShipment((MasterShipment) input).ErrorCode : ShipEngineComponents.DataAccessService.Modify(input, error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        error = new Error(4);
        return 4;
      }
    }

    public int Insert(object input, Error error)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Insert(input, error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        error = new Error(4);
        return 4;
      }
    }

    public int Delete(object input, Error error)
    {
      try
      {
        if (!(input is Shipment))
          return ShipEngineComponents.DataAccessService.Delete(input, error);
        Shipment shipment = (Shipment) input;
        ServiceResponse serviceResponse = ShipEngineComponents.ShipEngine.DeleteShipment(shipment.MasterTrackingNumber.TrackingNbr, shipment.Account.MeterNumber, shipment.Account.AccountNumber);
        error.Copy(serviceResponse.Error);
        return serviceResponse.ErrorCode;
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        error = new Error(4);
        return 4;
      }
    }

    public ServiceResponse Modify(object input)
    {
      try
      {
        return input is MasterShipment ? ShipEngineComponents.ShipEngine.ModifyMasterShipment((MasterShipment) input) : ShipEngineComponents.DataAccessService.Modify(input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ModifyShipment(Shipment shipment)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.ModifyShipment(shipment);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ModifyActualShipment(Shipment shipment)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Modify((object) shipment);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse Insert(object input)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Insert(input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse InsertWithoutValidation(object input)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.InsertWithoutValidation(input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse Delete(object input)
    {
      try
      {
        if (!(input is Shipment))
          return ShipEngineComponents.DataAccessService.Delete(input);
        Shipment shipment = (Shipment) input;
        return ShipEngineComponents.ShipEngine.DeleteShipment(shipment.MasterTrackingNumber.TrackingNbr, shipment.Account.MeterNumber, shipment.Account.AccountNumber);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public CommodityDescriptionsResponse GetIPDCommodityDescriptions(
      string accountNumber,
      string meterNumberstring,
      string MAWBNbr)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.GetIPDCommodityDescriptions(accountNumber, meterNumberstring, MAWBNbr);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new CommodityDescriptionsResponse(new Error(4));
      }
    }

    public GsmCursor CreateCursor(object obj)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.CreateCursor(obj);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return (GsmCursor) null;
      }
    }

    public GsmCursor CreateCursor<T>(T obj, int cursorSpec)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.CreateCursor<T>(obj, cursorSpec);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return (GsmCursor) null;
      }
    }

    public GsmCursor CreateCursor<T>(T obj)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.CreateCursor<T>(obj);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return (GsmCursor) null;
      }
    }

    public GsmCursor CreateCursor(object obj, int cursorSpec)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.CreateCursor(obj, cursorSpec);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return (GsmCursor) null;
      }
    }

    public int GetDataList(
      GsmDataAccess.ListSpecification listType,
      out DataTable output,
      Error error)
    {
      return ShipEngineComponents.DataAccessService.GetDataList((FedEx.Gsm.ShipEngine.Entities.ListSpecification) listType, out output, error);
    }

    public int GetDataList(FedEx.Gsm.ShipEngine.Entities.ListSpecification listType, out DataTable output, Error error)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.GetDataList(listType, out output, error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        error = new Error(4);
        output = new DataTable();
        return 4;
      }
    }

    public int GetDataList(
      object filter,
      GsmDataAccess.ListSpecification listType,
      out DataSet output,
      Error error)
    {
      return ShipEngineComponents.DataAccessService.GetDataList(filter, (FedEx.Gsm.ShipEngine.Entities.ListSpecification) listType, out output, error);
    }

    public int GetDataList(
      object filter,
      FedEx.Gsm.ShipEngine.Entities.ListSpecification listType,
      out DataSet output,
      Error error)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.GetDataList(filter, listType, out output, error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        error = new Error(4);
        output = new DataSet();
        return 4;
      }
    }

    public int GetDataList(
      object filter,
      GsmDataAccess.ListSpecification listType,
      out DataSet output,
      List<GsmFilter> filterList,
      List<GsmSort> sortList,
      List<string> columnNames,
      Error error)
    {
      return ShipEngineComponents.DataAccessService.GetDataList(filter, (FedEx.Gsm.ShipEngine.Entities.ListSpecification) listType, out output, filterList, sortList, columnNames, error);
    }

    public int GetDataList(
      object filter,
      FedEx.Gsm.ShipEngine.Entities.ListSpecification listType,
      out DataSet output,
      List<GsmFilter> filterList,
      List<GsmSort> sortList,
      List<string> columnNames,
      Error error)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.GetDataList(filter, listType, out output, filterList, sortList, columnNames, error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        error = new Error(4);
        output = new DataSet();
        return 4;
      }
    }

    public int GetDataList(
      object filter,
      GsmDataAccess.ListSpecification listType,
      out DataTable output,
      Error error)
    {
      return ShipEngineComponents.DataAccessService.GetDataList(filter, (FedEx.Gsm.ShipEngine.Entities.ListSpecification) listType, out output, error);
    }

    public int GetDataList(
      object filter,
      FedEx.Gsm.ShipEngine.Entities.ListSpecification listType,
      out DataTable output,
      Error error)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.GetDataList(filter, listType, out output, error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        error = new Error(4);
        output = new DataTable();
        return 4;
      }
    }

    public int GetDataList(
      object filter,
      GsmDataAccess.ListSpecification listType,
      out DataTable output,
      List<GsmFilter> filterList,
      List<GsmSort> sortList,
      List<string> columnNames,
      Error error)
    {
      return ShipEngineComponents.DataAccessService.GetDataList(filter, (FedEx.Gsm.ShipEngine.Entities.ListSpecification) listType, out output, filterList, sortList, columnNames, error);
    }

    public int GetDataList(
      object filter,
      FedEx.Gsm.ShipEngine.Entities.ListSpecification listType,
      out DataTable output,
      List<GsmFilter> filterList,
      List<GsmSort> sortList,
      List<string> columnNames,
      Error error)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.GetDataList(filter, listType, out output, filterList, sortList, columnNames, error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        error = new Error(4);
        output = new DataTable();
        return 4;
      }
    }

    public DataTableResponse GetDataList(GsmDataAccess.ListSpecification listType)
    {
      try
      {
        Error error = new Error();
        DataTable output;
        ShipEngineComponents.DataAccessService.GetDataList((FedEx.Gsm.ShipEngine.Entities.ListSpecification) listType, out output, error);
        return new DataTableResponse(output, error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new DataTableResponse(new DataTable(), new Error(4));
      }
    }

    public DataTableResponse GetDataList(FedEx.Gsm.ShipEngine.Entities.ListSpecification listType)
    {
      try
      {
        Error error = new Error();
        DataTable output;
        ShipEngineComponents.DataAccessService.GetDataList(listType, out output, error);
        return new DataTableResponse(output, error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new DataTableResponse(new DataTable(), new Error(4));
      }
    }

    public DataSetResponse GetDataSetList(object filter, GsmDataAccess.ListSpecification listType)
    {
      try
      {
        Error error = new Error();
        DataSet output;
        ShipEngineComponents.DataAccessService.GetDataList(filter, (FedEx.Gsm.ShipEngine.Entities.ListSpecification) listType, out output, error);
        if (output != null)
          output.RemotingFormat = SerializationFormat.Binary;
        return new DataSetResponse(output, error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new DataSetResponse(new DataSet(), new Error(4));
      }
    }

    public DataSetResponse GetDataSetList(object filter, FedEx.Gsm.ShipEngine.Entities.ListSpecification listType)
    {
      try
      {
        Error error = new Error();
        DataSet output;
        ShipEngineComponents.DataAccessService.GetDataList(filter, listType, out output, error);
        if (output != null)
          output.RemotingFormat = SerializationFormat.Binary;
        return new DataSetResponse(output, error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new DataSetResponse(new DataSet(), new Error(4));
      }
    }

    public DataSetResponse GetDataSetList(
      object filter,
      GsmDataAccess.ListSpecification listType,
      List<GsmFilter> filterList,
      List<GsmSort> sortList,
      List<string> columnNames)
    {
      try
      {
        Error error = new Error();
        DataSet output;
        ShipEngineComponents.DataAccessService.GetDataList(filter, (FedEx.Gsm.ShipEngine.Entities.ListSpecification) listType, out output, filterList, sortList, columnNames, error);
        if (output != null)
          output.RemotingFormat = SerializationFormat.Binary;
        return new DataSetResponse(output, error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new DataSetResponse(new DataSet(), new Error(4));
      }
    }

    public DataSetResponse GetDataSetList(
      object filter,
      FedEx.Gsm.ShipEngine.Entities.ListSpecification listType,
      List<GsmFilter> filterList,
      List<GsmSort> sortList,
      List<string> columnNames)
    {
      try
      {
        Error error = new Error();
        DataSet output;
        ShipEngineComponents.DataAccessService.GetDataList(filter, listType, out output, filterList, sortList, columnNames, error);
        if (output != null)
          output.RemotingFormat = SerializationFormat.Binary;
        return new DataSetResponse(output, error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new DataSetResponse(new DataSet(), new Error(4));
      }
    }

    public DataTableResponse GetDataTableList(
      object filter,
      GsmDataAccess.ListSpecification listType)
    {
      try
      {
        Error error = new Error();
        DataTable output;
        ShipEngineComponents.DataAccessService.GetDataList(filter, (FedEx.Gsm.ShipEngine.Entities.ListSpecification) listType, out output, error);
        return new DataTableResponse(output, error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new DataTableResponse(new DataTable(), new Error(4));
      }
    }

    public DataTableResponse GetDataTableList(object filter, FedEx.Gsm.ShipEngine.Entities.ListSpecification listType)
    {
      try
      {
        Error error = new Error();
        DataTable output;
        ShipEngineComponents.DataAccessService.GetDataList(filter, listType, out output, error);
        return new DataTableResponse(output, error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new DataTableResponse(new DataTable(), new Error(4));
      }
    }

    public DataTableResponse GetDataTableList(
      object filter,
      GsmDataAccess.ListSpecification listType,
      List<GsmFilter> filterList,
      List<GsmSort> sortList,
      List<string> columnNames)
    {
      try
      {
        Error error = new Error();
        DataTable output;
        ShipEngineComponents.DataAccessService.GetDataList(filter, (FedEx.Gsm.ShipEngine.Entities.ListSpecification) listType, out output, filterList, sortList, columnNames, error);
        return new DataTableResponse(output, error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new DataTableResponse(new DataTable(), new Error(4));
      }
    }

    public DataTableResponse GetDataTableList(
      object filter,
      FedEx.Gsm.ShipEngine.Entities.ListSpecification listType,
      List<GsmFilter> filterList,
      List<GsmSort> sortList,
      List<string> columnNames)
    {
      try
      {
        Error error = new Error();
        DataTable output;
        ShipEngineComponents.DataAccessService.GetDataList(filter, listType, out output, filterList, sortList, columnNames, error);
        return new DataTableResponse(output, error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new DataTableResponse(new DataTable(), new Error(4));
      }
    }

    public bool IsOnlineArchiveTime(string accountNumber, string meterNumber)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.IsOnlineArchiveTime(accountNumber, meterNumber);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return false;
      }
    }

    public bool IsOfflinePurgeTime(string accountNumber, string meterNumber)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.IsOfflinePurgeTime(accountNumber, meterNumber);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return false;
      }
    }

    public bool IsMinPurgeTimeForFreight(string accountNumber, string meterNumber)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.IsMinPurgeTimeForFreight(accountNumber, meterNumber);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return false;
      }
    }

    public bool IsFreightPurgeTime(string accountNumber, string meterNumber)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.IsFreightPurgeTime(accountNumber, meterNumber);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return false;
      }
    }

    public int ArchiveOnlineShipments(string accountNumber, string meterNumber)
    {
      int num = 0;
      try
      {
        num = ShipEngineComponents.DataAccessService.ArchiveOnlineShipments(accountNumber, meterNumber);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
      }
      return num;
    }

    public bool PurgeOfflineShipments(string accountNumber, string meterNumber)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.PurgeOfflineShipments(accountNumber, meterNumber);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return false;
      }
    }

    public ServiceResponse PurgeFreightShipment(
      string accountNumber,
      string meterNumber,
      bool byMinDays,
      out Error error)
    {
      error = (Error) null;
      try
      {
        ShipEngineComponents.DataAccessService.PurgeFreightShipment(accountNumber, meterNumber, byMinDays, out error);
        return new ServiceResponse(error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        error = new Error();
        return new ServiceResponse(error);
      }
    }

    public ServiceResponse PurgeSmartPostShipments(string accountNumber, string meterNumber)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.PurgeSmartPostShipments(accountNumber, meterNumber);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse();
      }
    }

    public bool DeleteAllRows(object input, Error error)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.DeleteAllRows(input, error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return false;
      }
    }

    public ServiceResponse DeleteAllRows(object input)
    {
      try
      {
        Error error = new Error();
        ShipEngineComponents.DataAccessService.DeleteAllRows(input, error);
        return new ServiceResponse(error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public bool BackupGSMAccountData(string pathname, Error error)
    {
      bool flag1 = false;
      try
      {
        if (FedEx.Gsm.ShipEngine.ShipEngineFacade.ShipEngine._isShipmentInProgress)
          return flag1;
        FedEx.Gsm.ShipEngine.ShipEngineFacade.ShipEngine._isAccountBackupInProgress = true;
        lock (FedEx.Gsm.ShipEngine.ShipEngineFacade.ShipEngine.dbBackupSyncLock)
        {
          bool flag2 = ShipEngineComponents.DataAccessService.BackupGSMAccountData(pathname, error);
          FedEx.Gsm.ShipEngine.ShipEngineFacade.ShipEngine._isAccountBackupInProgress = false;
          return flag2;
        }
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return false;
      }
    }

    public bool GetShipmentsToUpload(
      string accountNumber,
      string meterNumber,
      Shipment.CarrierType carrier,
      Error error)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.GetShipmentsToUpload(accountNumber, meterNumber, carrier, error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        error = new Error(4);
        return false;
      }
    }

    public string GetMasterTrackingNumber(
      string accountNumber,
      string meterNumber,
      string trackingNumber,
      out Error error)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.GetMasterTrackingNumber(accountNumber, meterNumber, trackingNumber, out error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        error = new Error(4);
        return "";
      }
    }

    public void SetContextOnline()
    {
      try
      {
        ShipEngineComponents.DataAccessService.SetContextOnline();
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
      }
    }

    public bool GetReportDataList(
      string ViewSpec,
      List<string> ViewColumns,
      List<GsmFilter> FilterList,
      List<GsmSort> SortList,
      out DataTable OutputList,
      Error error)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.GetReportDataList(ViewSpec, ViewColumns, FilterList, SortList, out OutputList, error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        OutputList = new DataTable();
        error = new Error(4);
        return false;
      }
    }

    public bool GetReportDataList(
      string ViewSpec,
      List<string> ViewColumns,
      List<GsmFilter> FilterList,
      List<GsmSort> SortList,
      out DataTable OutputList,
      Error error,
      bool isHistoryDB)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.GetReportDataList(ViewSpec, ViewColumns, FilterList, SortList, out OutputList, error, isHistoryDB);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        OutputList = new DataTable();
        error = new Error(4);
        return false;
      }
    }

    public bool GetServiceOptionsForReport(
      string sFedExAcctNbr,
      string sMeterNumber,
      string sTrackingNbrs,
      string sOfferingIDs,
      out DataTable OutputList,
      Error error)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.GetServiceOptionsForReport(sFedExAcctNbr, sMeterNumber, sTrackingNbrs, sOfferingIDs, out OutputList, error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        OutputList = new DataTable();
        error = new Error(4);
        return false;
      }
    }

    public bool IsCountryInShare(string countrycode)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.IsCountryInShare(countrycode);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return false;
      }
    }

    public List<string> GetAllSpecialServiceOfferingIDsForReports(out Error error)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.GetAllSpecialServiceOfferingIDsForReports(out error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        error = new Error(4);
        return new List<string>();
      }
    }

    public bool IsImageDocUsedByAnyCustomLabel(string imageId, out Error error)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.IsImageDocUsedByAnyCustomLabel(imageId, out error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        error = new Error(4);
        return false;
      }
    }

    public int GetReprintLabelCount(
      string account,
      string meter,
      string trackingNbr,
      out Error error)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.GetReprintLabelCount(account, meter, trackingNbr, out error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        error = new Error(4);
        return 0;
      }
    }

    public bool UpdateReprintLabelCount(
      string account,
      string meter,
      string trackingNbr,
      out Error error)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.UpdateReprintLabelCount(account, meter, trackingNbr, out error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        error = new Error(4);
        return false;
      }
    }

    public bool UpdateReprintLabelCount(
      string account,
      string meter,
      string trackingNbr,
      int increase,
      out Error error)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.UpdateReprintLabelCount(account, meter, trackingNbr, increase, out error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        error = new Error(4);
        return false;
      }
    }

    public int GetReprintFreightLabelCount(string freightAccount, string shipId, out Error error)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.GetReprintFreightLabelCount(freightAccount, shipId, out error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        error = new Error(4);
        return 0;
      }
    }

    public bool UpdateReprintFreightLabelCount(
      string freightAccount,
      string shipId,
      out Error error)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.UpdateReprintFreightLabelCount(freightAccount, shipId, out error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        error = new Error(4);
        return false;
      }
    }

    public bool GetCustomLabeAndValidatorCode(
      out List<string> custCode,
      out List<string> validatorCode,
      Error error)
    {
      custCode = new List<string>();
      validatorCode = new List<string>();
      try
      {
        return ShipEngineComponents.DataAccessService.GetCustomLabeAndValidatorCode(out custCode, out validatorCode, error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        error = new Error(4);
        return false;
      }
    }

    public bool ConsolidateIPDCommodities(
      string accountNumber,
      string meterNumber,
      string mawbNumber,
      out Error error)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.ConsolidateIPDCommodities(accountNumber, meterNumber, mawbNumber, out error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        error = new Error(4);
        return false;
      }
    }

    public ServiceResponse UpdateCloseReportingIndicator(
      string cycleNumber,
      CloseReportingIndicatorReportType reportType,
      bool value)
    {
      return reportType == CloseReportingIndicatorReportType.close ? ShipEngineComponents.ShipEngine.SetCloseInfoEODReportInd(cycleNumber, (short) value) : ShipEngineComponents.ShipEngine.SetCloseInfoInvoicePrinted(cycleNumber, value);
    }

    public ServiceResponse UpdateSPCloseReportingIndicator(
      string account,
      string meter,
      int cycleSequence,
      bool value)
    {
      return ShipEngineComponents.ShipEngine.SetCloseInfoInvoicePrinted(account, meter, cycleSequence, value);
    }

    public HoldFileTrackNbrResponse GetTrackNbrCountForHoldFile(HoldFileTrackNbrRequest reqIn)
    {
      return ShipEngineComponents.DataAccessService.GetTrackNbrCountForHoldFile(reqIn);
    }

    public ShipmentResponse GetRate(Shipment shipment)
    {
      if (ShipEngineCommonUtilities.IsEngineOnly())
      {
        Error error = new Error();
        Account account = ShipEngineComponents.DataAccessService.GetAccount(shipment.Account.AccountNumber, shipment.Account.MeterNumber, error);
        if (account != null)
          shipment.Account = new Account(account);
      }
      return ShipEngineComponents.ShipEngine.GetRate(shipment);
    }

    public TransitTimeAndRateResponse GetRateAndTransitTime(TransitTimeAndRateRequest request)
    {
      return ShipEngineComponents.ShipEngine.GetRateAndTransitTime(request);
    }

    public List<RateReplyDetailOnline> GetCompleteRateAndTransitTime(
      TransitTimeAndRateRequest request)
    {
      return ShipEngineComponents.ShipEngine.GetCompleteRateAndTransitTime(request);
    }

    public ServiceResponse GetHubDetail(string hubID, out SmartPostHub spHub)
    {
      try
      {
        return ShipEngineComponents.RateService.GetHubDetail(hubID, out spHub);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        spHub = new SmartPostHub();
        return new ServiceResponse(new Error(4));
      }
    }

    public CustomerDataResponse GetCustData(CustomerData customerData)
    {
      try
      {
        return ShipEngineComponents.RateService.GetCustData(customerData);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new CustomerDataResponse(new CustomerData(), new Error(4));
      }
    }

    public ServiceResponse InitializeRating(string accountnumber, string meterNumber, string type)
    {
      try
      {
        return ShipEngineComponents.RateService.InitializeRating(accountnumber, meterNumber, type);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse InitializeRating(
      string accountnumber,
      string meterNumber,
      string type,
      ref DllFileInfo dllFileInfo)
    {
      try
      {
        return ShipEngineComponents.RateService.InitializeRating(accountnumber, meterNumber, type, ref dllFileInfo);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse GetCommonRateServerVersion(ref string version)
    {
      try
      {
        return ShipEngineComponents.RateService.GetCommonRateServerVersion(ref version);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ImportGroundRates(string groundDiscountFile)
    {
      try
      {
        return ShipEngineComponents.RateService.ImportGroundRates(groundDiscountFile);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse CalcDimWeight(Shipment shipment, int divisor)
    {
      try
      {
        return ShipEngineComponents.RateService.CalcDimWeight(shipment, divisor);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse GetZone(Shipment shipment, ref string zone)
    {
      try
      {
        return ShipEngineComponents.RateService.GetZone(shipment, ref zone);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ValidateRateTables(
      string accountnumber,
      string meterNumber,
      string type)
    {
      try
      {
        return ShipEngineComponents.RateService.ValidateRateTables(accountnumber, meterNumber, type);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ZeroRateErrorResponse VerifyRatesAvailableStatus()
    {
      ZeroRateErrorResponse rateErrorResponse = new ZeroRateErrorResponse();
      try
      {
        return ShipEngineComponents.RateService.VerifyRatesAvailableStatus();
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return rateErrorResponse;
      }
    }

    public ZeroRateErrorResponse VerifyAcctRatesAvailableStatus(
      string accountnumber,
      string meterNumber,
      bool isGroundEnabled)
    {
      try
      {
        return ShipEngineComponents.RateService.VerifyAcctRatesAvailableStatus(accountnumber, meterNumber, isGroundEnabled);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
      }
      return new ZeroRateErrorResponse();
    }

    public bool GetExpiredRateSettingStatus()
    {
      bool rateSettingStatus = false;
      try
      {
        rateSettingStatus = ShipEngineComponents.RateService.GetExpiredRateSettingStatus();
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
      }
      return rateSettingStatus;
    }

    public void SetExpiredRateSettingStatus(bool isExpiredRateEnabled)
    {
      try
      {
        ShipEngineComponents.RateService.SetExpiredRateSettingStatus(isExpiredRateEnabled);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
      }
    }

    public StringListResponse GetCities(string countryCode)
    {
      try
      {
        return ShipEngineComponents.RouteService.GetCities(countryCode);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new StringListResponse(new List<string>(), new Error(4));
      }
    }

    public CityListResponse GetCities(string countryCode, string stateCode)
    {
      try
      {
        return ShipEngineComponents.RouteService.GetCities(countryCode, stateCode);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new CityListResponse(new Error(4));
      }
    }

    public CountryListResponse GetCountryList()
    {
      try
      {
        return ShipEngineComponents.EditService.GetCountryList();
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new CountryListResponse(new List<Country>(), new Error(4));
      }
    }

    public ServiceResponse ReloadDangerousGoodsTables()
    {
      try
      {
        return new ServiceResponse();
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(2, ex.Message));
      }
    }

    public DataTableResponse GetTAWBCountryList()
    {
      try
      {
        return ShipEngineComponents.EditService.GetTAWBCountryList();
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new DataTableResponse(new DataTable(), new Error(4));
      }
    }

    public DataTableResponse GetIAWBCountryList()
    {
      try
      {
        return ShipEngineComponents.EditService.GetIAWBCountryList();
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new DataTableResponse(new DataTable(), new Error(4));
      }
    }

    public DataTableResponse GetOriginCountryList(Country.Region region)
    {
      try
      {
        return ShipEngineComponents.EditService.GetOriginCountryList(region);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new DataTableResponse(new DataTable(), new Error(4));
      }
    }

    public DataTableResponse GetCountryOfMfgList()
    {
      try
      {
        return ShipEngineComponents.EditService.GetCountryOfMfgList();
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new DataTableResponse(new DataTable(), new Error(4));
      }
    }

    public ServiceResponse IsCountryInRegion(Country.Region region, string _country)
    {
      try
      {
        return ShipEngineComponents.EditService.IsCountryInRegion(region, _country);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public Recipient.ECITypeCode GetCountryECIType(string _country)
    {
      try
      {
        return ShipEngineComponents.EditService.GetCountryECIType(_country);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return Recipient.ECITypeCode.CI_NOT_ALLOWED;
      }
    }

    public string GetCountryNumber(string _countryCode)
    {
      try
      {
        return ShipEngineComponents.EditService.GetCountryNumber(_countryCode);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return "";
      }
    }

    public double GetDocumentMinDCV(string countryCode)
    {
      try
      {
        return ShipEngineComponents.EditService.GetDocumentMinDCV(countryCode);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return 0.0;
      }
    }

    public bool IsLocalLanguageRequired(string countryCode, out string languageCode)
    {
      try
      {
        return ShipEngineComponents.EditService.IsLocalLanguageRequired(countryCode, out languageCode);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        languageCode = "";
        return false;
      }
    }

    public bool IsCountryWayBillOnly(
      string originCountryCode,
      string destinationCountryCode,
      out int errorCode)
    {
      try
      {
        return ShipEngineComponents.EditService.IsCountryWayBillOnly(originCountryCode, destinationCountryCode, out errorCode);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        errorCode = 4;
        return false;
      }
    }

    public bool IsCountryVerificationRequired(
      string countryCode,
      Account account,
      out int errorCode)
    {
      try
      {
        return ShipEngineComponents.EditService.IsCountryVerificationRequired(countryCode, account, out errorCode);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        errorCode = 4;
        return false;
      }
    }

    public string GetLACCurrency(string countryCode)
    {
      try
      {
        return ShipEngineComponents.EditService.GetLACCurrency(countryCode);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return "";
      }
    }

    public string GetLACCurrencyCode(string countryCode)
    {
      try
      {
        return ShipEngineComponents.EditService.GetLACCurrencyCode(countryCode);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return "";
      }
    }

    public bool GetLACIsInsightAvailable(string countryCode)
    {
      try
      {
        return ShipEngineComponents.EditService.GetLACIsInsightAvailable(countryCode);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return false;
      }
    }

    public int GetLACCICount(string countryCode)
    {
      try
      {
        return ShipEngineComponents.EditService.GetLACCICount(countryCode);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return 4;
      }
    }

    public int GetLACDefaultDocLabelCount(string countryCode)
    {
      try
      {
        return ShipEngineComponents.EditService.GetLACDefaultDocLabelCount(countryCode);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return 4;
      }
    }

    public int GetLACDefaultNonDocLabelCount(string countryCode)
    {
      try
      {
        return ShipEngineComponents.EditService.GetLACDefaultNonDocLabelCount(countryCode);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return 4;
      }
    }

    public DataTableResponse GetPaymentList(Payment.ChargeType charge, Shipment shipment)
    {
      try
      {
        Error theError = new Error();
        int num = shipment.IsIntl ? 1 : 0;
        if (shipment.TDInd != Shipment.TransBorderType.Child)
        {
          int tdInd = (int) shipment.TDInd;
        }
        if (string.IsNullOrEmpty(shipment.PkgType))
          shipment.PkgType = "01";
        return string.IsNullOrEmpty(shipment.ServiceCode) ? new DataTableResponse((DataTable) null, theError) : ShipEngineComponents.EditService.GetPaymentList(charge, shipment);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new DataTableResponse((DataTable) null, new Error(4));
      }
    }

    public List<FreightShipment.PaymentType> GetFreightPaymentTypes(bool billToAccount)
    {
      try
      {
        return ShipEngineComponents.EditService.GetFreightPaymentTypes(billToAccount);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new List<FreightShipment.PaymentType>();
      }
    }

    public List<FreightShipment.PaymentTerms> GetFreightPaymentTerms(
      FreightShipment.PaymentType paymentType)
    {
      try
      {
        return ShipEngineComponents.EditService.GetFreightPaymentTerms(paymentType);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new List<FreightShipment.PaymentTerms>();
      }
    }

    public DataTableResponse GetClearanceFacilityList()
    {
      try
      {
        return ShipEngineComponents.EditService.GetClearanceFacilityList();
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new DataTableResponse(new DataTable(), new Error(4));
      }
    }

    public DataTableResponse GetIPDImporterOfRecordList(
      string serviceCode,
      string recipientCountry,
      bool isSPOC)
    {
      try
      {
        return ShipEngineComponents.EditService.GetIPDImporterOfRecordList(serviceCode, recipientCountry, isSPOC);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new DataTableResponse(new DataTable(), new Error(4));
      }
    }

    public StringListResponse GetIPDCountries(string sLocID)
    {
      return ShipEngineComponents.EditService.GetIPDCountries(sLocID);
    }

    public RouteResponse RoutePackage(Shipment shipment)
    {
      try
      {
        Error errorOut = new Error();
        RouteResponse routeResponse = ShipEngineComponents.ShipEngine.RoutePackage(shipment);
        ShipEngineComponents.ShipEngine.HandleEditRouteError(routeResponse.Error, errorOut);
        routeResponse.Error = errorOut;
        return routeResponse;
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new RouteResponse(new Route(), new Error(4));
      }
    }

    public RouteResponse RoutePackage(RouterFilter routeFilter)
    {
      try
      {
        Error errorOut = new Error();
        RouteResponse routeResponse = ShipEngineComponents.RouteService.RoutePackage(routeFilter);
        ShipEngineComponents.ShipEngine.HandleEditRouteError(routeResponse.Error, errorOut);
        routeResponse.Error = errorOut;
        return routeResponse;
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new RouteResponse(new Route(), new Error(4));
      }
    }

    public bool IsCountryPostalAware(string countryCode)
    {
      try
      {
        return ShipEngineComponents.RouteService.IsCountryPostalAware(countryCode);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return false;
      }
    }

    public bool IsETDAllowedForCountry(
      string countryCode,
      ETDAvailabilityData.CountryType countryType)
    {
      try
      {
        return ShipEngineComponents.EditService.IsETDAllowedForCountry(countryCode, countryType);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return false;
      }
    }

    public ETDResponse GetETDInformationForOD(string origCountryCode, string destCountryCode)
    {
      try
      {
        return ShipEngineComponents.EditService.GetETDInformationForOD(origCountryCode, destCountryCode);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ETDResponse();
      }
    }

    public ETDResponse GetETDDocumentTypes()
    {
      try
      {
        return ShipEngineComponents.EditService.GetETDDocumentTypes();
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ETDResponse();
      }
    }

    public ServiceResponse ValidateCountryServed(string countryCode)
    {
      try
      {
        Error errorOut = new Error();
        ServiceResponse serviceResponse = ShipEngineComponents.EditService.ValidateCountryServed(countryCode);
        ShipEngineComponents.ShipEngine.HandleEditRouteError(serviceResponse.Error, errorOut);
        serviceResponse.Error = errorOut;
        return serviceResponse;
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ValidateStateProvince(string countryCode, string stateProvince)
    {
      try
      {
        Error errorOut = new Error();
        ServiceResponse serviceResponse = ShipEngineComponents.EditService.ValidateStateProvince(countryCode, stateProvince);
        ShipEngineComponents.ShipEngine.HandleEditRouteError(serviceResponse.Error, errorOut);
        serviceResponse.Error = errorOut;
        return serviceResponse;
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    [Obsolete("This method is deprecated. Use ValidatePostal(Address address, Address.AddressType addressType) instead")]
    public ServiceResponse ValidatePostal(Address address)
    {
      try
      {
        Error errorOut = new Error();
        ServiceResponse serviceResponse = ShipEngineComponents.EditService.ValidatePostal(address);
        ShipEngineComponents.ShipEngine.HandleEditRouteError(serviceResponse.Error, errorOut);
        serviceResponse.Error = errorOut;
        return serviceResponse;
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ValidatePostal(Address address, Address.AddressType addressType)
    {
      try
      {
        Error errorOut = new Error();
        ServiceResponse serviceResponse = ShipEngineComponents.EditService.ValidatePostal(address, addressType);
        ShipEngineComponents.ShipEngine.HandleEditRouteError(serviceResponse.Error, errorOut);
        serviceResponse.Error = errorOut;
        return serviceResponse;
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public PostalInformationResponse GetValidatedPostal(
      Address address,
      Address.AddressType addressType)
    {
      return this.GetValidatedPostal(address, addressType, false);
    }

    public PostalInformationResponse GetValidatedPostal(
      Address address,
      Address.AddressType addressType,
      bool bIsDomestic)
    {
      PostalInformationResponse validatedPostal = new PostalInformationResponse();
      Error errorOut = new Error();
      try
      {
        ShipEngineComponents.ShipEngine.HandleEditRouteError(ShipEngineComponents.EditService.ValidatePostal(address, addressType, bIsDomestic).Error, errorOut);
        validatedPostal.Error = errorOut;
        validatedPostal.ValidatedAddress = address;
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        validatedPostal.Error = new Error(4);
      }
      return validatedPostal;
    }

    public PostalInformationResponse ValidatePostalInformation(
      PostalInformationRequest postalInformationRequest)
    {
      Error errorOut = new Error();
      PostalInformationResponse informationResponse = new PostalInformationResponse();
      try
      {
        string countryNumber = ShipEngineComponents.EditService.GetCountryNumber(postalInformationRequest.CountryCode);
        if (!string.IsNullOrEmpty(countryNumber))
          postalInformationRequest.ValidationAddress.CountryNumber = countryNumber;
        informationResponse = ShipEngineComponents.EditService.ValidatePostalInformation(postalInformationRequest);
        ShipEngineComponents.ShipEngine.HandleEditRouteError(informationResponse.Error, errorOut);
        informationResponse.Error = errorOut;
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        informationResponse.Error = new Error(4);
      }
      return informationResponse;
    }

    public ServiceResponse ValidatePhoneNumber(
      string countryCode,
      string phoneNumber,
      string phoneExtension)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidatePhoneNumber(countryCode, phoneNumber, phoneExtension);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ValidateEmailAddress(string emailAddress)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidateEmailAddress(emailAddress);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ValidateImporterOfRecord(Recipient recipient)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidateImporterOfRecord(recipient);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ValidateBuyerInfo(Customer buyer)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidateBuyerInfo(buyer);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ValidateFICE(Shipment shipment)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidateFICE(shipment);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public IPDIORResponse ValidateIPDIOR(IPDIORRequest ipdIOR)
    {
      return ShipEngineComponents.EditService.ValidateIPDIOR(ipdIOR);
    }

    public ServiceResponse ValidateCommodity(CommodityRequest commodityRequest)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidateCommodity(commodityRequest);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public int ValidateClearanceInformation(
      ManagedeSRGWrapper.ClearanceInfoType typ,
      string sOrigin,
      string sDestination,
      ref List<string> text)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidateClearanceInformation(typ, sOrigin, sDestination, ref text);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return 4;
      }
    }

    public int ValidateDangerousGoods(string sOrigin, string sDestination)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidateDangerousGoods(sOrigin, sDestination);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return 4;
      }
    }

    public int DocumentRequirements(string sOrigin, string sDestination, ref List<string> infoText)
    {
      try
      {
        return ShipEngineComponents.EditService.DocumentRequirements(sOrigin, sDestination, ref infoText);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return 4;
      }
    }

    public int POBoxText(string sOrigin, string sDestination, ref List<string> infoText)
    {
      try
      {
        return ShipEngineComponents.EditService.POBoxText(sOrigin, sDestination, ref infoText);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return 4;
      }
    }

    public int ValidateProhibitedItems(string sOrigin, string sDestination, ref List<string> text)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidateProhibitedItems(sOrigin, sDestination, ref text);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return 4;
      }
    }

    public ServiceResponse ValidateDryIce(Shipment shipment)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidateDryIce(shipment);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ValidateDryIce(DryIce dryIce)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidateDryIce(dryIce);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public int ValidateGiftShipment(
      string sOrigin,
      string sDestination,
      ref string giftShipmentAllowed)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidateGiftShipment(sOrigin, sDestination, ref giftShipmentAllowed);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return 4;
      }
    }

    public int HolidayInfo(string sOrigin, string sDestination, ref List<string> infoText)
    {
      try
      {
        return ShipEngineComponents.EditService.HolidayInfo(sOrigin, sDestination, ref infoText);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return 4;
      }
    }

    public int IPFIEFHalRoute(
      ManagedeSRGWrapper.IPIEHalType typ,
      string sOrigin,
      string sDestination,
      ref List<string> IPFIEFHalInfo)
    {
      try
      {
        return ShipEngineComponents.EditService.IPFIEFHalRoute(typ, sOrigin, sDestination, ref IPFIEFHalInfo);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return 4;
      }
    }

    public int IPFIEFHalRamp(
      ManagedeSRGWrapper.IPIEHalType typ,
      string sOrigin,
      string sDestination,
      ref List<string> IPFIEFHalInfo)
    {
      try
      {
        return ShipEngineComponents.EditService.IPFIEFHalRamp(typ, sOrigin, sDestination, ref IPFIEFHalInfo);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return 4;
      }
    }

    public int IPFIEFHalCityName(
      ManagedeSRGWrapper.IPIEHalType typ,
      string sOrigin,
      string sDestination,
      ref List<string> IPFIEFHalInfo)
    {
      try
      {
        return ShipEngineComponents.EditService.IPFIEFHalCityName(typ, sOrigin, sDestination, ref IPFIEFHalInfo);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return 4;
      }
    }

    public int IPFIEFHalLabelName(
      ManagedeSRGWrapper.IPIEHalType typ,
      string sOrigin,
      string sDestination,
      ref List<string> IPFIEFHalInfo)
    {
      try
      {
        return ShipEngineComponents.EditService.IPFIEFHalLabelName(typ, sOrigin, sDestination, ref IPFIEFHalInfo);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return 4;
      }
    }

    public int IPFIEFHalServiceName(
      ManagedeSRGWrapper.IPIEHalType typ,
      string sOrigin,
      string sDestination,
      ref List<string> IPFIEFHalInfo)
    {
      try
      {
        return ShipEngineComponents.EditService.IPFIEFHalServiceName(typ, sOrigin, sDestination, ref IPFIEFHalInfo);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return 4;
      }
    }

    public int IPFIEFHalUrsaCode(
      ManagedeSRGWrapper.IPIEHalType typ,
      string sOrigin,
      string sDestination,
      ref List<string> IPFIEFHalInfo)
    {
      try
      {
        return ShipEngineComponents.EditService.IPFIEFHalUrsaCode(typ, sOrigin, sDestination, ref IPFIEFHalInfo);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return 4;
      }
    }

    public int IPIEHalLabelName(
      ManagedeSRGWrapper.IPIEHalType typ,
      string sOrigin,
      string sDestination,
      ref List<string> IPFIEFHalInfo)
    {
      try
      {
        return ShipEngineComponents.EditService.IPFIEFHalLabelName(typ, sOrigin, sDestination, ref IPFIEFHalInfo);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return 4;
      }
    }

    public int IPIEHalUrsaCode(
      ManagedeSRGWrapper.IPIEHalType typ,
      string sOrigin,
      string sDestination,
      ref List<string> IPFIEFHalInfo)
    {
      try
      {
        return ShipEngineComponents.EditService.IPIEHalUrsaCode(typ, sOrigin, sDestination, ref IPFIEFHalInfo);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return 4;
      }
    }

    public ServiceResponse ValidateDimsForShipment(Shipment shipment)
    {
      try
      {
        Error error = new Error(1);
        int num = shipment.IsIntl ? 1 : 0;
        if (shipment.TDInd != Shipment.TransBorderType.Child)
        {
          int tdInd = (int) shipment.TDInd;
        }
        return ShipEngineComponents.EditService.ValidateDimsForShipment(shipment);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public int ValidateMaxDims(
      string sOrigin,
      string sDestination,
      string serviceName,
      string InsOrCms,
      double girth,
      double length,
      ref double maxGirth,
      ref double maxLength)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidateMaxDims(sOrigin, sDestination, serviceName, InsOrCms, girth, length, ref maxGirth, ref maxLength);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return 4;
      }
    }

    public int ValidateMaxValue(
      string sOrigin,
      string sDestination,
      double value,
      string serviceName)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidateMaxValue(sOrigin, sDestination, value, serviceName);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return 4;
      }
    }

    public int ValidateMaxWeight(
      string sOrigin,
      string sDestination,
      double value,
      string serviceName,
      string KgsOrLbs)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidateMaxWeight(sOrigin, sDestination, value, serviceName, KgsOrLbs);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return 4;
      }
    }

    public ServiceResponse SetUVSDKFiles(
      string ursaDirectory,
      string[] ursaFiles,
      string[] editFiles)
    {
      try
      {
        return ShipEngineComponents.RouteService.SetUVSDKFiles(ursaDirectory, ursaFiles, editFiles);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse SetSRGFilePath(string filePath)
    {
      try
      {
        return ShipEngineComponents.RouteService.SetSRGFilePath(filePath);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ValidateRecipient(Recipient recipient)
    {
      try
      {
        if (string.IsNullOrEmpty(recipient.Address.CountryNumber))
          recipient.Address.CountryNumber = this.GetCountryNumber(recipient.Address.CountryCode);
        return ShipEngineComponents.EditService.ValidateRecipient(recipient);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ValidateRecipient(RecipientRequest recipientReq)
    {
      try
      {
        if (string.IsNullOrEmpty(recipientReq.Recipient.Address.CountryNumber))
          recipientReq.Recipient.Address.CountryNumber = this.GetCountryNumber(recipientReq.Recipient.Address.CountryCode);
        return ShipEngineComponents.EditService.ValidateRecipient(recipientReq);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse GetCountryVerification(
      string originCountryCode,
      string destCountryCountry)
    {
      try
      {
        return ShipEngineComponents.EditService.GetCountryVerification(originCountryCode, destCountryCountry);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public SpecialServicesResponse GetSpecialServiceList(Shipment shipment)
    {
      try
      {
        Error error1 = new Error();
        int num = shipment.IsIntl ? 1 : 0;
        if (shipment.TDInd != Shipment.TransBorderType.Child)
        {
          int tdInd = (int) shipment.TDInd;
        }
        if (ShipEngineCommonUtilities.IsEngineOnly())
        {
          Error error2 = new Error();
          Account account = ShipEngineComponents.DataAccessService.GetAccount(shipment.Account.AccountNumber, shipment.Account.MeterNumber, error2);
          if (account != null)
            shipment.Account = new Account(account);
        }
        if (shipment.TDInd != Shipment.TransBorderType.Child && shipment.TDInd != Shipment.TransBorderType.Master)
          return ShipEngineComponents.EditService.GetSpecialServiceList(shipment);
        Shipment shipment1 = (Shipment) shipment.Clone();
        ServiceResponse serviceResponse = ShipEngineComponents.ShipEngine.SetSenderForTD(shipment1);
        if (serviceResponse.HasError)
          return new SpecialServicesResponse(new List<SplSvc>(), serviceResponse.Error);
        shipment1.IsIntl = false;
        return ShipEngineComponents.EditService.GetSpecialServiceList(shipment1);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new SpecialServicesResponse(new List<SplSvc>(), new Error(4));
      }
    }

    public SpecialServicesResponse GetAllSpecialServiceList()
    {
      try
      {
        return ShipEngineComponents.EditService.GetAllSpecialServiceList();
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new SpecialServicesResponse(new List<SplSvc>(), new Error(4));
      }
    }

    public StringListResponse GetDateCertainDates(Shipment shipment)
    {
      try
      {
        return ShipEngineComponents.EditService.GetDateCertainDates(shipment);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new StringListResponse(new List<string>(), new Error(4));
      }
    }

    public ServiceResponse ValidateAccountNumber(string accountNumber)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidateAccountNumber(accountNumber);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ValidateSender(SenderRequest senderRequest)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidateSender(senderRequest);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public bool IsValidCurrentSender(
      SenderRequest senderRequest,
      bool bWSEDinProgress,
      out Error error)
    {
      try
      {
        return ShipEngineComponents.EditService.IsValidCurrentSender(senderRequest, bWSEDinProgress, out error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        error = new Error(4);
        return false;
      }
    }

    public ServiceResponse ValidateObject(object target, int validationType)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidateObject(target, validationType);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse GetDGListFromID(DGInfoList dgList)
    {
      try
      {
        return ShipEngineComponents.EditService.GetDGListFromID(dgList);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse GetDGListFromID(ref DGInfoList dgList)
    {
      try
      {
        return ShipEngineComponents.EditService.GetDGListFromID(dgList);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse SumDGQValue(ref double Qvalue, double netQty, double maxQty)
    {
      try
      {
        return ShipEngineComponents.EditService.SumDGQValue(ref Qvalue, netQty, maxQty);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public CommOverrideResponse ValidateCommOverrideCode(string meterNumber, string overrideCode)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidateCommOverrideCode(meterNumber, overrideCode);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new CommOverrideResponse(CommOverrideResponse.OverrideCodeType.Invalid, new Error(4));
      }
    }

    public ServiceResponse ValidateIPDImporterOfRecord(IPDIORRequest ipdIOR)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidateIPDImporterOfRecord(ipdIOR);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ValidateShipProfile(ShipProfile profile)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidateShipProfile(profile);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ValidateHazardousMaterials(GroundHazMat hazMat)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidateHazardousMaterials(hazMat);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ValidateHazardousMaterials(GroundHazMat hazMat, bool addToDB)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidateHazardousMaterials(hazMat, addToDB);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ValidateConsignee(Recipient recipient)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidateConsignee(recipient);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ValidateInboundReceiving(InboundReceive inBoundReceive)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidateInboundReceiving(inBoundReceive);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ValidateShipment(Shipment shipment)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidateShipment(shipment);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ValidatePackageSize(Dimension dims, bool isDbaseAddModify)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidatePackageSize(dims, isDbaseAddModify);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ValidateDepartment(Department dept)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidateDepartment(dept);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ValidateBroker(Broker broker)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidateBroker(broker);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ValidateBroker(Broker broker, bool bAddModify)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidateBroker(broker, bAddModify);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ValidateIntlPackagingType(string packageType)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidateIntlPackagingType(packageType);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ValidateSignatureOption(Shipment shipment)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidateSignatureOption(shipment);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ValidateHAL(Shipment shipment)
    {
      try
      {
        if (shipment.TDInd != Shipment.TransBorderType.None)
          shipment.IsIntl = false;
        return ShipEngineComponents.EditService.ValidateHAL(shipment);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ValidateCOD(CODRequest codReq)
    {
      try
      {
        Error error = new Error();
        return ShipEngineComponents.EditService.ValidateCOD(codReq);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ValidateAlcohol(SpecialServiceOptions alcohol)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidateAlcohol(alcohol);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ValidateAlcohol(Alcohol alcohol)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidateAlcohol(alcohol);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ValidateAlcohol(
      Shipment shipment,
      bool bAlcoholFlag,
      bool bSignatureReleaseFlag)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidateAlcohol(shipment, bAlcoholFlag, bSignatureReleaseFlag);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ValidateAlcohol(Shipment shipment, bool IsShiptime)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidateAlcohol(shipment, IsShiptime);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ValidateSystem(SystemInfo sysInfo)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidateSystem(sysInfo);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ValidateShippingContents(Commodity commodity)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidateShippingContents(commodity);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ValidateCODRemittor(Sender sender)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidateCODRemittor(sender);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ValidatePIB(PIB pib)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidatePIB(pib);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ValidateSED(SEDInfo sedInfo, string senderCountryCode)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidateSED(sedInfo, senderCountryCode);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ValidateHandlingInformation(HandlingInfoRequest handlingInfoRequest)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidateHandlingInformation(handlingInfoRequest);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ValidateIPDMAWB(Shipment shipment)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidateIPDMAWB(shipment);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ValidateIPDConfirmationInfo(Shipment shipment)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidateIPDConfirmationInfo(shipment);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ValidateIPDDashboard(Shipment shipment)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidateIPDDashboard(shipment);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ValidateAdditionalCharges(
      CommercialInvoice commercialInvoice,
      string termsOfSale)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidateAdditionalCharges(commercialInvoice, termsOfSale);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ValidateShipDate(
      DateTime shipDate,
      bool isUnlimitedFutureDay,
      Shipment.CarrierType carrier)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidateShipDate(shipDate, isUnlimitedFutureDay, carrier);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ValidateRateQuoteData(Shipment ship)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidateRateQuoteData(ship);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ValidateIDFSpecialServices(IDFPieceCntVerify pcv, bool IDFPCVEnabled)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidateIDFSpecialServices(pcv, IDFPCVEnabled);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ValidateExpressExtraServiceMessage(
      ExtraServiceMessageRequest extraServiceRequest)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidateExpressExtraServiceMessage(extraServiceRequest);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ValidateGroundAccountNumber(string groundAcctNumber)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidateGroundAccountNumber(groundAcctNumber);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ValidateAccount(Account account, Account masterAccount)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidateAccount(account, masterAccount);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ValidateLTLFreightAccount(FreightAccount freightAccount)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidateLTLFreightAccount(freightAccount);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ValidateAccountDetails(Account account)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidateAccountDetails(account);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ValidateGroundTrackingNumberRange(TrackingNumber trackingNumber)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidateGroundTrackingNumberRange(trackingNumber);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ValidateSelectDayDelivery(
      Shipment.CarrierType carrier,
      string service,
      string primaryPhoneNumber,
      string altPhoneNumber,
      DateTime selectDate,
      DateTime shipDate)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidateSelectDayDelivery(carrier, service, primaryPhoneNumber, altPhoneNumber, selectDate, shipDate);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ValidateNAFTACertificateOfOrigin(
      NaftaCO producer,
      NaftaCO.NaftaProducerType producerType)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidateNAFTACertificateOfOrigin(producer, producerType);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ValidateNAFTAOriginProducer(Address producerAddr)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidateNAFTAOriginProducer(producerAddr);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ValidateNAFTAExporter(Address exporterAddr)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidateNAFTAExporter(exporterAddr);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ValidateAppointmentDelivery(Shipment shipment)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidateAppointmentDelivery(shipment);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ValidateRenameFieldsForm(SystemPrefs prefs)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidateRenameFieldsForm(prefs);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ValidateTrackByNumberOrReference(TrackRequest trackRequest)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidateTrackByNumberOrReference(trackRequest);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ValidateTrackByHistory(TrackRequest trackRequest)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidateTrackByHistory(trackRequest);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ValidateDuplicateTrackingDetails(TrackRequest trackRequest)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidateDuplicateTrackingDetails(trackRequest);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ValidateSPODRequest(TrackSPODRequest trackRequest)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidateSPODRequest(trackRequest);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public string GetPortOfUnladingValue(Shipment shipment)
    {
      try
      {
        return ShipEngineComponents.EditService.GetPortOfUnladingValue(shipment);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return "";
      }
    }

    public string GetPortOfUnladingValue(
      Shipment.CarrierType carrier,
      string recipientPostal,
      string recipientState,
      string senderState)
    {
      try
      {
        return ShipEngineComponents.EditService.GetPortOfUnladingValue(carrier, recipientPostal, recipientState, senderState);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return "";
      }
    }

    public string GetExpressPortOfExport(
      string destCountry,
      string destState,
      string origState,
      string origPostal)
    {
      try
      {
        return ShipEngineComponents.EditService.GetExpressPortOfExport(destCountry, destState, origState, origPostal);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return "";
      }
    }

    public string GetGroundPortOfExport(string destCountry, string destState, string origState)
    {
      try
      {
        return ShipEngineComponents.EditService.GetGroundPortOfExport(destCountry, destState, origState);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return "";
      }
    }

    public string GetPortOfExportValue(Shipment shipment)
    {
      try
      {
        return ShipEngineComponents.EditService.GetPortOfExportValue(shipment);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return "";
      }
    }

    public PolicyGridPortDeterminationResponse GetPortOfDetermination(Shipment shipment)
    {
      try
      {
        return ShipEngineComponents.EditService.GetPortOfDetermination(shipment);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new PolicyGridPortDeterminationResponse();
      }
    }

    public bool IsRequiredToCallPortDeterminationPolicyGrid(
      Shipment shipment,
      FreightShipment frShipment)
    {
      try
      {
        return ShipEngineComponents.EditService.IsRequiredToCallPortDeterminationPolicyGrid(shipment, frShipment);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return false;
      }
    }

    public Error GetPackagingConversionRulesPolicyGridInfo(ref Shipment shipment)
    {
      try
      {
        return ShipEngineComponents.EditService.GetPackagingConversionRulesPolicyGridInfo(ref shipment);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new Error(0);
      }
    }

    public DataTableResponse GetFICETable()
    {
      try
      {
        return ShipEngineComponents.EditService.GetFICETable();
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new DataTableResponse(new DataTable(), new Error(4));
      }
    }

    public int GetPortOfArrivalByState(string state, out string[] stringList)
    {
      try
      {
        return ShipEngineComponents.EditService.GetPortOfArrivalByState(state, out stringList);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        stringList = new string[0];
        return 4;
      }
    }

    public int GetPortOfArrivalList(out string[] stringList)
    {
      try
      {
        return ShipEngineComponents.EditService.GetPortOfArrivalList(out stringList);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        stringList = new string[0];
        return 4;
      }
    }

    public ServiceResponse ValidatePaymentType(
      string serviceCode,
      Payment.PaymentType paymentType,
      Payment.ChargeType chargeType,
      string accountNumber,
      bool bAcctHasCollect,
      string origCtry,
      string destCtryCode,
      string destCtryName,
      bool bIntl,
      Shipment.CarrierType carrier,
      Shipment.ReturnShipmentType rtnShipType,
      Shipment.TransBorderType tdShipType)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidatePaymentType(serviceCode, paymentType, chargeType, accountNumber, bAcctHasCollect, origCtry, destCtryCode, destCtryName, bIntl, carrier, rtnShipType, tdShipType);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public SpecialServicesResponse GetPreferenceSpecialServiceList(
      Account account,
      FieldPref.PreferenceTypes preferenceType)
    {
      try
      {
        return ShipEngineComponents.EditService.GetPreferenceSpecialServiceList(account, preferenceType);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new SpecialServicesResponse(new List<SplSvc>(), new Error(4));
      }
    }

    public DataTableResponse PreferencePaymentList(
      Account account,
      bool bInternational,
      Payment.ChargeType chargeType,
      Shipment.CarrierType carrierType)
    {
      try
      {
        return ShipEngineComponents.EditService.PreferencePaymentList(account, bInternational, chargeType, carrierType);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new DataTableResponse(new DataTable(), new Error(4));
      }
    }

    public ServiceResponse ValidateB13A(
      ref Shipment.B13aFilingOption B13AFilingOption,
      string exportStatementData,
      bool isDocument,
      double customsValue,
      string destCountry,
      string currencyType)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidateB13A(ref B13AFilingOption, exportStatementData, isDocument, customsValue, destCountry, currencyType);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public bool ValidateB13AValueEdit(ref Shipment shipment)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidateB13AValueEdit(ref shipment);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return false;
      }
    }

    public ServiceResponse ValidateTDDashboard(Account account, MasterShipment masterShipment)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidateTDDashboard(account, masterShipment);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public MaxCountryWeightResponse GetMaxCountryWeight(string countryCode)
    {
      return ShipEngineComponents.EditService.GetMaxCountryWeight(countryCode);
    }

    public MultipleLabelsPerCountryResponse GetMinLabelCountPerCountryOfOrigin(string OriginCountry)
    {
      try
      {
        return ShipEngineComponents.EditService.GetMinLabelCountPerCountryOfOrigin(OriginCountry);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new MultipleLabelsPerCountryResponse(new Error(4));
      }
    }

    public PolicyResponse GetDocumentDeterminationPolicyGridInfo(PolicyRequest request)
    {
      try
      {
        return ShipEngineComponents.ShipEngine.GetDocumentDeterminationPolicyGridInfo(request);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        PolicyResponse determinationPolicyGridInfo = new PolicyResponse();
        if (determinationPolicyGridInfo.Error == null)
          determinationPolicyGridInfo.Error = new Error();
        determinationPolicyGridInfo.Error.Code = 4;
        return determinationPolicyGridInfo;
      }
    }

    public PolicyResponse GetDocumentFeaturesPolicyGridInfo(PolicyRequest request)
    {
      try
      {
        return ShipEngineComponents.ShipEngine.GetDocumentFeaturesPolicyGridInfo(request);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        PolicyResponse featuresPolicyGridInfo = new PolicyResponse();
        if (featuresPolicyGridInfo.Error == null)
          featuresPolicyGridInfo.Error = new Error();
        featuresPolicyGridInfo.Error.Code = 4;
        return featuresPolicyGridInfo;
      }
    }

    public PolicyResponse GetNumberOfCopiesPolicyGridInfo(PolicyRequest request)
    {
      try
      {
        return ShipEngineComponents.EditService.GetNumberOfCopiesPolicyGridInfo(request);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        PolicyResponse copiesPolicyGridInfo = new PolicyResponse();
        if (copiesPolicyGridInfo.Error == null)
          copiesPolicyGridInfo.Error = new Error();
        copiesPolicyGridInfo.Error.Code = 4;
        return copiesPolicyGridInfo;
      }
    }

    public ETDResponse GetETDAvailabilityInformation(Shipment shipment)
    {
      try
      {
        return ShipEngineComponents.EditService.GetETDAvailabilityInformation(shipment);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        ETDResponse availabilityInformation = new ETDResponse();
        if (availabilityInformation.Error == null)
          availabilityInformation.Error = new Error();
        availabilityInformation.Error.Code = 4;
        return availabilityInformation;
      }
    }

    public PolicyResponse GetAllETDPolicyGridInfo(PolicyRequest pr)
    {
      try
      {
        return ShipEngineComponents.ShipEngine.GetAllETDPolicyGridInfo(pr);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        PolicyResponse etdPolicyGridInfo = new PolicyResponse();
        if (etdPolicyGridInfo.Error == null)
          etdPolicyGridInfo.Error = new Error();
        etdPolicyGridInfo.Error.Code = 4;
        return etdPolicyGridInfo;
      }
    }

    public bool IsIPDAllowedfromPolicyGrid(Account account)
    {
      try
      {
        return ShipEngineComponents.ShipEngine.IsIPDAllowedfromPolicyGrid(account);
      }
      catch (Exception ex)
      {
        return false;
      }
    }

    public PolicyGridCommonResponse GetDimensionRulesFromPolicyGrid(Shipment shipment)
    {
      try
      {
        return ShipEngineComponents.ShipEngine.GetDimensionRulesFromPolicyGrid(shipment);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new PolicyGridCommonResponse();
      }
    }

    public PolicyGridCommonResponse GetBookingSpaceFromPolicyGrid(Shipment shipment)
    {
      try
      {
        return ShipEngineComponents.ShipEngine.GetBookingSpaceFromPolicyGrid(shipment);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new PolicyGridCommonResponse();
      }
    }

    public ServicePackageResponse GetServicePackaging(ServicePackageRequest servPkgRequest)
    {
      try
      {
        return ShipEngineComponents.ShipEngine.GetServicePackaging(servPkgRequest);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServicePackageResponse();
      }
    }

    public List<Svc> GetServiceNamesForReport(List<Svc> svcList)
    {
      try
      {
        return ShipEngineComponents.ShipEngine.GetServiceNamesForReport(svcList);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new List<Svc>();
      }
    }

    public bool FindIDDCustomEntry(
      string account,
      string meternumber,
      string shipId,
      out DataTable dtCustomEntries,
      Error error)
    {
      try
      {
        return ShipEngineComponents.ShipEngine.FindIDDCustomEntry(account, meternumber, shipId, out dtCustomEntries, error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        dtCustomEntries = new DataTable();
        error = new Error(4);
        return false;
      }
    }

    public Error GetOfferingIDForNonDedicatedHandlingCode(
      PolicyGridEpicEnterpriseRequest request,
      out string offeringId)
    {
      try
      {
        return ShipEngineComponents.ShipEngine.GetOfferingIDForNonDedicatedHandlingCode(request, out offeringId);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        offeringId = (string) null;
        return new Error(0);
      }
    }

    public SplSvc GetSpecialServiceForHandlingCode(string handlingCode, string languageCd)
    {
      try
      {
        return ShipEngineComponents.ShipEngine.GetSpecialServiceForHandlingCode(handlingCode, languageCd);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return (SplSvc) null;
      }
    }

    public CommodityDescriptionValidationResponse CommodityDescAndHSCodeValidation(Shipment shipment)
    {
      CommodityDescriptionValidationResponse validationResponse = new CommodityDescriptionValidationResponse();
      try
      {
        validationResponse = ShipEngineComponents.EditService.CommodityDescAndHSCodeValidation(shipment);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        validationResponse.Error = new Error(0);
      }
      return validationResponse;
    }

    public CommodityDescriptionValidationResponse CommodityDescAndHSCodeValidationForFreight(
      FreightShipment freightshipment)
    {
      CommodityDescriptionValidationResponse validationResponse = new CommodityDescriptionValidationResponse();
      try
      {
        validationResponse = ShipEngineComponents.EditService.CommodityDescAndHSCodeValidationForFreight(freightshipment);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        validationResponse.Error = new Error(0);
      }
      return validationResponse;
    }

    public bool IsHSCodeSearchEnabled(Shipment shipment, FreightShipment frShipment)
    {
      try
      {
        return ShipEngineComponents.EditService.IsHSCodeSearchEnabled(shipment, frShipment);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return false;
      }
    }

    public bool IsRequiredToCallVagueCommodityDescriptionCheck(
      Shipment shipment,
      FreightShipment frShipment)
    {
      try
      {
        return ShipEngineComponents.EditService.IsRequiredToCallVagueCommodityDescriptionCheck(shipment, frShipment);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return false;
      }
    }

    public Error CommodityAndDocumentVagueDescriptionValidation(CommodityRequest commodityRequest)
    {
      Error error = new Error(1);
      try
      {
        return ShipEngineComponents.EditService.CommodityAndDocumentVagueDescriptionValidation(commodityRequest);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
      }
      return error;
    }

    public string GetCustMsgFrmShipmentContentPolicy(Shipment shipment)
    {
      string empty = string.Empty;
      try
      {
        return ShipEngineComponents.EditService.GetCustMsgFrmShipmentContentPolicy(shipment);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
      }
      return empty;
    }

    public FilingOptionsListResponse GetFilingOptions(string culture)
    {
      try
      {
        return ShipEngineComponents.EditService.GetFilingOptions(culture);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
      }
      return new FilingOptionsListResponse();
    }

    public ExemptionsListResponse GetExemptions(string culture)
    {
      try
      {
        return ShipEngineComponents.EditService.GetExemptions(culture);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
      }
      return new ExemptionsListResponse();
    }

    public string GetDocNonDocFrmShipmentContentPolicy(Shipment shipment)
    {
      string empty = string.Empty;
      try
      {
        return ShipEngineComponents.EditService.GetDocNonDocFrmShipmentContentPolicy(shipment);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
      }
      return empty;
    }

    public CascadeFIDValidationResponse GetCascadeFIDResponse(Shipment shipment)
    {
      CascadeFIDValidationResponse cascadeFidResponse = new CascadeFIDValidationResponse();
      try
      {
        return ShipEngineComponents.EditService.GetCascadeFIDResponse(shipment);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
      }
      return cascadeFidResponse;
    }

    public bool IsRequiredToCallFIDValidation(
      Shipment shipment,
      FreightShipment frShipment,
      List<RegulatoryCapabilities> capabilities)
    {
      bool callFidValidation = false;
      try
      {
        callFidValidation = ShipEngineComponents.EditService.IsRequiredToCallFIDValidation(shipment, frShipment, capabilities);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
      }
      return callFidValidation;
    }

    public int GetAssociationCount(ShipmentRequest shipmentRequest)
    {
      try
      {
        return DataAccessServiceImp.GetInstance().GetAssociationCount(shipmentRequest);
      }
      catch (Exception ex)
      {
        return 0;
      }
    }

    public ServiceResponse DeleteAccountMeter(Account accountMeterToDelete)
    {
      try
      {
        return DataAccessServiceImp.GetInstance().DeleteAccountMeter(accountMeterToDelete);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse DeleteShipment(
      string trackingNumber,
      string meterNumber,
      string accountNumber)
    {
      try
      {
        return ShipEngineComponents.ShipEngine.DeleteShipment(trackingNumber, meterNumber, accountNumber);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public bool UpdateETDInformation(Shipment shipment, out Error error)
    {
      try
      {
        error = new Error(1);
        return ShipEngineComponents.DataAccessService.UpdateETDInformation(shipment, out error);
      }
      catch (Exception ex)
      {
        error = new Error(4);
        error.Message = ex.Message + " " + ex.InnerException?.ToString();
        return false;
      }
    }

    public bool RemoveETDFromShipment(Shipment shipment, out Error error)
    {
      try
      {
        error = new Error(1);
        return ShipEngineComponents.DataAccessService.RemoveETDFromShipment(shipment, out error);
      }
      catch (Exception ex)
      {
        error = new Error(4);
        error.Message = ex.Message + " " + ex.InnerException?.ToString();
        return false;
      }
    }

    public bool IsAnyMeterSmartPostEnabled()
    {
      try
      {
        return ShipEngineComponents.DataAccessService.IsAnyMeterSmartPostEnabled();
      }
      catch (Exception ex)
      {
        return false;
      }
    }

    public bool InsertMissingDefaultTemplates(Error error)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.InsertMissingDefaultTemplates(error);
      }
      catch (Exception ex)
      {
        return false;
      }
    }

    public List<AutoTrackInfo> GetAutoTrackInfoList(string account, string meter, out Error error)
    {
      try
      {
        error = new Error(1);
        return ShipEngineComponents.DataAccessService.GetAutoTrackInfoList(account, meter, out error);
      }
      catch (Exception ex)
      {
        error = new Error(4);
        error.Message = ex.Message + " " + ex.InnerException?.ToString();
        return new List<AutoTrackInfo>();
      }
    }

    public bool DeleteAutoTrackInfo(AutoTrackInfo autoInfo, out Error error)
    {
      try
      {
        error = new Error(1);
        return ShipEngineComponents.DataAccessService.DeleteAutoTrackInfo(autoInfo, out error);
      }
      catch (Exception ex)
      {
        error = new Error(4);
        error.Message = ex.Message + " " + ex.InnerException?.ToString();
        return false;
      }
    }

    public bool DeleteAutoTrackInfo(List<AutoTrackInfo> autoInfos, out Error error)
    {
      try
      {
        error = new Error(1);
        return ShipEngineComponents.DataAccessService.DeleteAutoTrackInfo(autoInfos, out error);
      }
      catch (Exception ex)
      {
        error = new Error(4);
        error.Message = ex.Message + " " + ex.InnerException?.ToString();
        return false;
      }
    }

    public bool UpdateAutoTrackInfoStatus(AutoTrackInfo autoInfo, out Error error)
    {
      try
      {
        error = new Error(1);
        return ShipEngineComponents.DataAccessService.UpdateAutoTrackInfoStatus(autoInfo, out error);
      }
      catch (Exception ex)
      {
        error = new Error(4);
        error.Message = ex.Message + " " + ex.InnerException?.ToString();
        return false;
      }
    }

    public bool UpdateAutoTrackInfoStatus(
      AutoTrackInfo autoInfo,
      DateTime removeByDateTime,
      out Error error)
    {
      try
      {
        error = new Error(1);
        return ShipEngineComponents.DataAccessService.UpdateAutoTrackInfoStatus(autoInfo, removeByDateTime, out error);
      }
      catch (Exception ex)
      {
        error = new Error(4);
        error.Message = ex.Message + " " + ex.InnerException?.ToString();
        return false;
      }
    }

    public bool IsTimeForAutoTrackToStart(string acctNbr, string meter, out Error error)
    {
      try
      {
        error = new Error(1);
        return ShipEngineComponents.DataAccessService.IsTimeForAutoTrackToStart(acctNbr, meter, out error);
      }
      catch (Exception ex)
      {
        error = new Error(4);
        error.Message = ex.Message + " " + ex.InnerException?.ToString();
        return false;
      }
    }

    public List<AutoTrackInfo> GetAutoTrackInfoFreightList(
      string FreightAcctNumber,
      out Error error)
    {
      try
      {
        error = new Error(1);
        return ShipEngineComponents.DataAccessService.GetAutoTrackInfoFreightList(FreightAcctNumber, out error);
      }
      catch (Exception ex)
      {
        error = new Error(4);
        error.Message = ex.Message + " " + ex.InnerException?.ToString();
        return new List<AutoTrackInfo>();
      }
    }

    public List<AutoTrackInfo> GetAutoTrackInfoFreightList(
      string expressAcct,
      string expressMeter,
      out Error error)
    {
      try
      {
        error = new Error(1);
        return ShipEngineComponents.DataAccessService.GetAutoTrackInfoFreightList(expressAcct, expressMeter, out error);
      }
      catch (Exception ex)
      {
        error = new Error(4);
        error.Message = ex.Message + " " + ex.InnerException?.ToString();
        return new List<AutoTrackInfo>();
      }
    }

    public bool DeleteAutoTrackInfoFreight(AutoTrackInfo autoInfo, out Error error)
    {
      try
      {
        error = new Error(1);
        return ShipEngineComponents.DataAccessService.DeleteAutoTrackInfoFreight(autoInfo, out error);
      }
      catch (Exception ex)
      {
        error = new Error(4);
        error.Message = ex.Message + " " + ex.InnerException?.ToString();
        return false;
      }
    }

    public bool TruncateLog()
    {
      try
      {
        return ShipEngineComponents.DataAccessService.TruncateLog();
      }
      catch (Exception ex)
      {
        return false;
      }
    }

    public bool UpdateAutoTrackInfoFreightStatuss(AutoTrackInfo autoInfo, out Error error)
    {
      try
      {
        error = new Error(1);
        return ShipEngineComponents.DataAccessService.UpdateAutoTrackInfoFreightStatuss(autoInfo, out error);
      }
      catch (Exception ex)
      {
        error = new Error(4);
        error.Message = ex.Message + " " + ex.InnerException?.ToString();
        return false;
      }
    }

    public bool UpdateAutoTrackInfoFreightStatuss(
      AutoTrackInfo autoInfo,
      DateTime removeByDateTime,
      out Error error)
    {
      try
      {
        error = new Error(1);
        return ShipEngineComponents.DataAccessService.UpdateAutoTrackInfoFreightStatuss(autoInfo, removeByDateTime, out error);
      }
      catch (Exception ex)
      {
        error = new Error(4);
        error.Message = ex.Message + " " + ex.InnerException?.ToString();
        return false;
      }
    }

    public bool IsTimeForAutoTrackFreightToStart(string freightAcctNbr, out Error error)
    {
      try
      {
        error = new Error(1);
        return ShipEngineComponents.DataAccessService.IsTimeForAutoTrackFreightToStart(freightAcctNbr, out error);
      }
      catch (Exception ex)
      {
        error = new Error(4);
        error.Message = ex.Message + " " + ex.InnerException?.ToString();
        return false;
      }
    }

    public bool AddToAutoTrackInfoList(Shipment ship, out Error error)
    {
      try
      {
        error = new Error(1);
        return ShipEngineComponents.DataAccessService.AddToAutoTrackInfoList(ship, out error);
      }
      catch (Exception ex)
      {
        error = new Error(4);
        error.Message = ex.Message + " " + ex.InnerException?.ToString();
        return false;
      }
    }

    public bool AddToAutoTrackInfoFreightList(FreightShipment fShip, out Error error)
    {
      try
      {
        error = new Error(1);
        return ShipEngineComponents.DataAccessService.AddToAutoTrackInfoFreightList(fShip, out error);
      }
      catch (Exception ex)
      {
        error = new Error(4);
        error.Message = ex.Message + " " + ex.InnerException?.ToString();
        return false;
      }
    }

    public ServiceResponse CheckAutoTrackInfoLimit(List<AutoTrackInfo> items)
    {
      try
      {
        return ShipEngineComponents.EditService.CheckAutoTrackInfoLimit(items);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public HazmatLookupResponse DGHazmatLookup(HazMatCommodity hazmatCommodity)
    {
      try
      {
        return ShipEngineComponents.HazmatService.DGHazmatLookup(hazmatCommodity);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new HazmatLookupResponse(new Error(4));
      }
    }

    public Error LoadData()
    {
      try
      {
        return ShipEngineComponents.HazmatService.LoadData();
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new Error(4);
      }
    }

    public Error LoadData(string filename, string id)
    {
      try
      {
        return ShipEngineComponents.HazmatService.LoadData(filename, id);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new Error(4);
      }
    }

    public Error DGHazmatValidation(Shipment shipment)
    {
      try
      {
        return ShipEngineComponents.HazmatService.DGHazmatValidation(shipment);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new Error(4);
      }
    }

    public HazmatCommodityResponse HazmatCommodityValidation(Shipment shipment)
    {
      try
      {
        return ShipEngineComponents.HazmatService.HazmatCommodityValidation(shipment);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new HazmatCommodityResponse(new Error(4));
      }
    }

    public ServiceResponse KeepShipmentInSEDB(
      string accountNumber,
      string meterNumber,
      short numberOfDays)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.KeepShipmentInSEDB(accountNumber, meterNumber, numberOfDays);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public bool SetFutureDayUpload(
      string accountNumber,
      string meterNumber,
      bool bSetFutureDayUpload)
    {
      return ShipEngineComponents.DataAccessService.SetFutureDayUpload(accountNumber, meterNumber, bSetFutureDayUpload);
    }

    public Account RetrieveAccountMeterSettings(
      string meterNumber,
      string accountNumber,
      Error error)
    {
      try
      {
        if (FedEx.Gsm.ShipEngine.ShipEngineFacade.ShipEngine._isCreateEngineAllowed)
          return ShipEngineComponents.ShipEngine.RetrieveAccountMeterSettings(meterNumber, accountNumber, error);
        error.Code = 8001;
        error.Message = "Restore GSMAccountData is in progress, come back later";
        return (Account) null;
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new Account();
      }
    }

    public List<Account> RetrieveAllAccountMeterSettings(Error error)
    {
      try
      {
        return ShipEngineComponents.ShipEngine.RetrieveAllAccountMeterSettings(error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new List<Account>();
      }
    }

    public ServiceResponse IsValidLocation(string LocId, string CountryCode)
    {
      try
      {
        return ShipEngineComponents.EditService.IsValidLocation(LocId, CountryCode);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public int IsURSAValid()
    {
      try
      {
        return ShipEngineComponents.RouteService.IsURSAValid();
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return 4;
      }
    }

    public ServiceResponse GetURSAFileInformation(
      out string EffectiveDate,
      out string ExpirationDate,
      out string UvSDKVersion,
      out string SrgNumber,
      out string Status,
      out string Astra,
      out string Countries)
    {
      try
      {
        return ShipEngineComponents.RouteService.GetURSAFileInformation(out EffectiveDate, out ExpirationDate, out UvSDKVersion, out SrgNumber, out Status, out Astra, out Countries);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        EffectiveDate = ExpirationDate = UvSDKVersion = Status = Astra = Countries = SrgNumber = "";
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse GetURSAFileInformation(
      out string EffectiveDate,
      out string ExpirationDate,
      out string UvSDKVersion,
      out string SrgNumber,
      out string Status,
      out string Astra,
      out string Countries,
      out string CurrentURSAFilename)
    {
      try
      {
        return ShipEngineComponents.RouteService.GetURSAFileInformation(out EffectiveDate, out ExpirationDate, out UvSDKVersion, out SrgNumber, out Status, out Astra, out Countries, out CurrentURSAFilename);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        CurrentURSAFilename = EffectiveDate = ExpirationDate = UvSDKVersion = Status = Astra = Countries = SrgNumber = "";
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ValidateDGCommodity(DGCommodity commodity)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidateDGCommodity(commodity);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ValidateDGCommodity(DGCommodityRequest dgCommodity)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidateDGCommodity(dgCommodity);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ValidateIATA(Iata iata, int packages)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidateIATA(iata, packages);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ValidateIATA(ref Iata iata, int packages)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidateIATA(iata, packages);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ValidateIATA(ShipmentRequest shipmentRequest, bool isAddToShipment)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidateIATA(shipmentRequest, isAddToShipment);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ValidateIATA(ShipmentRequest shipmentRequest)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidateIATA(shipmentRequest);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public bool ValidateAESValueEdit(Shipment shipment)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidateAESValueEdit(shipment);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return false;
      }
    }

    public HALLookupResponse HoldAtLocationLookup(HALLookupRequest halLookupRequest)
    {
      try
      {
        return ShipEngineComponents.HALLookupService.HoldAtLocationLookup(halLookupRequest);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new HALLookupResponse(new Error(4));
      }
    }

    public CurrencyListResponse GetCurrencyList()
    {
      try
      {
        return ShipEngineComponents.EditService.GetCurrencyList();
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new CurrencyListResponse(new List<string>(), new Error(4));
      }
    }

    public CountryListResponse GetCountryProfileList()
    {
      try
      {
        return ShipEngineComponents.EditService.GetCountryProfileList();
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new CountryListResponse(new List<CountryProfile>(), new Error(4));
      }
    }

    public CurrencyListResponse GetCurrencyList(FreightShipment freightShipment)
    {
      try
      {
        return ShipEngineComponents.EditService.GetCurrencyList(freightShipment);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new CurrencyListResponse(new List<string>(), new Error(4));
      }
    }

    public CurrencyListResponse GetCurrencyList(Shipment shipment)
    {
      try
      {
        return ShipEngineComponents.EditService.GetCurrencyList(shipment);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new CurrencyListResponse(new List<string>(), new Error(4));
      }
    }

    public ServiceResponse IsCurrencyFileValid()
    {
      try
      {
        return ShipEngineComponents.EditService.IsCurrencyFileValid();
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return (ServiceResponse) new CurrencyListResponse(new List<string>(), new Error(4));
      }
    }

    public bool IsMeterDeletionAllowed(string meter, string account, out Error error)
    {
      try
      {
        return ShipEngineComponents.EditService.IsMeterDeletionAllowed(meter, account, out error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        error = new Error(0);
        return false;
      }
    }

    public Decimal GetCurrencyConversion(FedEx.Gsm.ShipEngine.Entities.Currency currencyInfo, out ServiceResponse response)
    {
      try
      {
        response = new ServiceResponse();
        return ShipEngineComponents.EditService.GetCurrencyConversion(currencyInfo, out response);
      }
      catch (Exception ex)
      {
        response = new ServiceResponse(0);
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return 0.00M;
      }
    }

    public HazmatCommodityResponse HazmatLookup(GroundHazMat hazmat)
    {
      try
      {
        return ShipEngineComponents.EditService.HazmatLookup(hazmat);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new HazmatCommodityResponse(new Error(4));
      }
    }

    public IsEUCountryResponse IsEUCountry(string countryCode)
    {
      try
      {
        return ShipEngineComponents.EditService.IsEUCountry(countryCode);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new IsEUCountryResponse(false);
      }
    }

    public IsEUCountryResponse InFreeCirculation(Address sender, Address recipient)
    {
      try
      {
        return ShipEngineComponents.EditService.InFreeCirculation(sender, recipient);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new IsEUCountryResponse(false);
      }
    }

    public IDDMinimisResponse GetMinimisValue()
    {
      try
      {
        return ShipEngineComponents.ShipEngine.GetMinimisValue();
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new IDDMinimisResponse(new Error(4));
      }
    }

    public ServiceResponse ValidateTrackingNumber(TrackingNumberRequest trackingNumberRequest)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidateTrackingNumber(trackingNumberRequest);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ValidateNonStandardPackage(ShipmentRequest shipmentRequest)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidateNonStandardPackage(shipmentRequest);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse GetUVSDKFiles(
      out string ursaDirectory,
      out string[] ursaFiles,
      out string[] editFiles)
    {
      try
      {
        return ShipEngineComponents.RouteService.GetUVSDKFiles(out ursaDirectory, out ursaFiles, out editFiles);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        ursaDirectory = "";
        ursaFiles = editFiles = new string[0];
        return new ServiceResponse(new Error(4));
      }
    }

    public SignatureOptionsResponse GetSignatureTypesList(Shipment shipment)
    {
      try
      {
        Error error = new Error();
        Account account = ShipEngineComponents.DataAccessService.GetAccount(shipment.Account.AccountNumber, shipment.Account.MeterNumber, error);
        if (account != null)
          shipment.Account = new Account(account);
        return ShipEngineComponents.EditService.GetSignatureTypesList(shipment);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new SignatureOptionsResponse(new List<SpecialServiceOptions.SignatureServiceOption>(), -1, new Error(4));
      }
    }

    public ServiceResponse ValidateGroup(RecipientGroup recipientGroup)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidateGroup(recipientGroup);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public List<string> GetDGPackageDims()
    {
      try
      {
        return ShipEngineComponents.EditService.GetDGPackageDims();
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new List<string>();
      }
    }

    public List<string> GetDGIataUnitsOfMeasure()
    {
      try
      {
        return ShipEngineComponents.EditService.GetDGIataUnitsOfMeasure();
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new List<string>();
      }
    }

    public List<string> GetDGPackagingTypes()
    {
      try
      {
        return ShipEngineComponents.EditService.GetDGPackagingTypes();
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new List<string>();
      }
    }

    public List<string> GetDGRadActivityUnits()
    {
      try
      {
        return ShipEngineComponents.EditService.GetDGRadActivityUnits();
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new List<string>();
      }
    }

    public List<string> GetDGNuclideList()
    {
      try
      {
        return ShipEngineComponents.EditService.GetDGNuclideList();
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new List<string>();
      }
    }

    public List<string> GetDGPhysicalForms()
    {
      try
      {
        return ShipEngineComponents.EditService.GetDGPhysicalForms();
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new List<string>();
      }
    }

    public ServiceResponse ValidateLabelType(
      out string labelType,
      double transportIndexValue,
      double serviceReadingValue)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidateLabelType(out labelType, transportIndexValue, serviceReadingValue);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        labelType = "";
        return new ServiceResponse(new Error(4));
      }
    }

    [Obsolete("Use BarcodeValidationResponse ValidateBarcode(string)")]
    public int ValidateASTRA(string astraStr)
    {
      return ShipEngineComponents.RouteService.ValidateBarcode(astraStr).Status;
    }

    public BarcodeValidationResponse ValidateBarcode(string astraStr)
    {
      return ShipEngineComponents.RouteService.ValidateBarcode(astraStr);
    }

    public ServiceResponse ValidateClearanceInfo(Shipment shipment)
    {
      return ShipEngineComponents.EditService.ValidateClearanceInfo(shipment);
    }

    public ServiceResponse ValidateDIA(Shipment shipment, bool isPassport)
    {
      return ShipEngineComponents.EditService.ValidateDIA(shipment, isPassport);
    }

    public ServiceResponse ValidateDIA(Shipment shipment)
    {
      return ShipEngineComponents.EditService.ValidateDIA(shipment);
    }

    public ServiceResponse ValidateDIAAddress(Sender address)
    {
      return ShipEngineComponents.EditService.ValidateDIAAddress(address);
    }

    public ServiceResponse ValidateLTLFreightBOL(FreightBillOfLadingLine currentBOLlineItem)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidateLTLFreightBOL(currentBOLlineItem);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ValidateLTLFreightBOL(
      FreightBillOfLadingLine currentBOLlineItem,
      bool isViewEdit)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidateLTLFreightBOL(currentBOLlineItem, isViewEdit);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ValidateITAR(Shipment shipment)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidateITAR(shipment);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public bool IsEEIFilingRequired(Shipment shipment)
    {
      try
      {
        return ShipEngineComponents.EditService.IsEEIFilingRequired(shipment);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return false;
      }
    }

    public bool IsHazAPISupported(Shipment shipment, string moduleType)
    {
      try
      {
        return ShipEngineComponents.EditService.IsHazAPISupported(shipment, moduleType);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return false;
      }
    }

    public bool IsFreightService(Shipment shipment)
    {
      try
      {
        return ShipEngineComponents.EditService.IsFreightService(shipment);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return false;
      }
    }

    public SpecialServicesResponse GetSpecialServiceListForIPDMawb(Shipment shipment)
    {
      try
      {
        if (ShipEngineCommonUtilities.IsEngineOnly())
        {
          Error error = new Error();
          Account account = ShipEngineComponents.DataAccessService.GetAccount(shipment.Account.AccountNumber, shipment.Account.MeterNumber, error);
          if (account != null)
            shipment.Account = new Account(account);
        }
        return ShipEngineComponents.EditService.GetSpecialServiceListForIPDMawb(shipment);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new SpecialServicesResponse(new List<SplSvc>(), new Error(4));
      }
    }

    public ValidateAddressAndBRClassificationResponse ValidateAddressAndBRClassification(
      ValidateAddressAndBRClassificationRequest validateAddressBRCClass)
    {
      try
      {
        if (validateAddressBRCClass.Account == null)
        {
          Account output = new Account();
          GsmDataAccess gsmDataAccess = new GsmDataAccess();
          Error error = new Error(1, "OperationOk");
          try
          {
            if (gsmDataAccess.Retrieve<Account>(new Account()
            {
              IsMaster = true
            }, out output, out error))
              validateAddressBRCClass.Account = output;
          }
          catch (Exception ex)
          {
            FxLogger.LogMessage(FedEx.Gsm.Common.Logging.LogLevel.Error, FxLogger.AppCode_AppLogic, "ValidateAddressAndBRClassification.Retrieve--get default account ", ex.Message);
            error.Code = 0;
            error.Message = ex.Message;
          }
        }
        return validateAddressBRCClass.Account != null && validateAddressBRCClass.Account.is_USE_API_INIT ? ShipEngineComponents.APIService.ValidateAddressAndBRClassification(validateAddressBRCClass, false) : ShipEngineComponents.ShipEngine.ValidateAddressAndBRClassification(validateAddressBRCClass);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ValidateAddressAndBRClassificationResponse();
      }
    }

    public ValidateMultipleAddressBRResponse ValidateMultipleAddressAndBRClassification(
      ValidateMultipleAddressBRRequest validateMultipleAddressBRCClass)
    {
      try
      {
        if (validateMultipleAddressBRCClass.Account == null)
        {
          Account output = new Account();
          GsmDataAccess gsmDataAccess = new GsmDataAccess();
          Error error = new Error(1, "OperationOk");
          try
          {
            if (gsmDataAccess.Retrieve<Account>(new Account()
            {
              IsMaster = true
            }, out output, out error))
              validateMultipleAddressBRCClass.Account = output;
          }
          catch (Exception ex)
          {
            FxLogger.LogMessage(FedEx.Gsm.Common.Logging.LogLevel.Error, FxLogger.AppCode_AppLogic, "ValidateMultipleAddressAndBRClassification.Retrieve--get default account ", ex.Message);
            error.Code = 0;
            error.Message = ex.Message;
          }
        }
        return validateMultipleAddressBRCClass.Account != null && validateMultipleAddressBRCClass.Account.is_USE_API_INIT ? ShipEngineComponents.APIService.ValidateMultipleAddressAndBRClassification(validateMultipleAddressBRCClass, false) : ShipEngineComponents.ShipEngine.ValidateMultipleAddressAndBRClassification(validateMultipleAddressBRCClass);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ValidateMultipleAddressBRResponse();
      }
    }

    public SHAREAddressLookupResponse GetAddressLookupResponse(
      SHAREAddressLookupRequest requestFilter)
    {
      try
      {
        return ShipEngineComponents.ShipEngine.GetAddressLookupResponse(requestFilter);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        SHAREAddressLookupResponse addressLookupResponse = new SHAREAddressLookupResponse();
        addressLookupResponse.Error = new Error(4);
        return addressLookupResponse;
      }
    }

    public CascadeHSSearchResponse GetResultsFromHsSearch(CascadeHSSearchRequest request)
    {
      try
      {
        return ShipEngineComponents.ShipEngine.GetResultsFromHsSearch(request);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        CascadeHSSearchResponse resultsFromHsSearch = new CascadeHSSearchResponse();
        resultsFromHsSearch.Error = new Error(4);
        return resultsFromHsSearch;
      }
    }

    public ValidateAddressAndBRClassificationResponse ValidateAddressAndBRClassification(
      ValidateAddressAndBRClassificationRequest validateAddressBRCClass,
      bool isShipTime)
    {
      try
      {
        if (validateAddressBRCClass.Account == null)
        {
          Account output = new Account();
          GsmDataAccess gsmDataAccess = new GsmDataAccess();
          Error error = new Error(1, "OperationOk");
          try
          {
            if (gsmDataAccess.Retrieve<Account>(new Account()
            {
              IsMaster = true
            }, out output, out error))
              validateAddressBRCClass.Account = output;
          }
          catch (Exception ex)
          {
            FxLogger.LogMessage(FedEx.Gsm.Common.Logging.LogLevel.Error, FxLogger.AppCode_AppLogic, "ValidateAddressAndBRClassification with isShipTime -- Retrieve--get default account ", ex.Message);
            error.Code = 0;
            error.Message = ex.Message;
          }
        }
        return validateAddressBRCClass.Account != null && validateAddressBRCClass.Account.is_USE_API_INIT ? ShipEngineComponents.APIService.ValidateAddressAndBRClassification(validateAddressBRCClass, isShipTime) : ShipEngineComponents.ShipEngine.ValidateAddressAndBRClassification(validateAddressBRCClass, isShipTime);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ValidateAddressAndBRClassificationResponse();
      }
    }

    public ValidateMultipleAddressBRResponse ValidateMultipleAddressAndBRClassification(
      ValidateMultipleAddressBRRequest validateMultipleAddressBRCClass,
      bool isShipTime)
    {
      try
      {
        if (validateMultipleAddressBRCClass.Account == null)
        {
          Account output = new Account();
          GsmDataAccess gsmDataAccess = new GsmDataAccess();
          Error error = new Error(1, "OperationOk");
          try
          {
            if (gsmDataAccess.Retrieve<Account>(new Account()
            {
              IsMaster = true
            }, out output, out error))
              validateMultipleAddressBRCClass.Account = output;
          }
          catch (Exception ex)
          {
            FxLogger.LogMessage(FedEx.Gsm.Common.Logging.LogLevel.Error, FxLogger.AppCode_AppLogic, "ValidateMultipleAddressAndBRClassification with isShipTime -- Retrieve--get default account ", ex.Message);
            error.Code = 0;
            error.Message = ex.Message;
          }
        }
        return validateMultipleAddressBRCClass.Account != null && validateMultipleAddressBRCClass.Account.is_USE_API_INIT ? ShipEngineComponents.APIService.ValidateMultipleAddressAndBRClassification(validateMultipleAddressBRCClass, isShipTime) : ShipEngineComponents.ShipEngine.ValidateMultipleAddressAndBRClassification(validateMultipleAddressBRCClass, isShipTime);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ValidateMultipleAddressBRResponse();
      }
    }

    public TrackingNumberInfoResponse GetNextTrackingNumber(
      string meter,
      string accountNumber,
      Shipment.CarrierType carrierType)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.GetNextTrackingNumber(meter, accountNumber, carrierType);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new TrackingNumberInfoResponse(new Error(4));
      }
    }

    public TrackingNumberInfoResponse GetNextNTrackingNumbers(
      string meterNumber,
      string accountNumber,
      int numberOfTrackingNumbers,
      Shipment.CarrierType carrierType)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.GetNextNTrackingNumbers(meterNumber, accountNumber, numberOfTrackingNumbers, carrierType);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new TrackingNumberInfoResponse(new Error(4));
      }
    }

    public ShipmentResponse GetShipmentInformation(
      string trackingNumber,
      string meterNumber,
      string accountNumber)
    {
      try
      {
        return ShipEngineComponents.ShipEngine.GetShipmentInformation(trackingNumber, meterNumber, accountNumber);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ShipmentResponse(new Error(4));
      }
    }

    [Obsolete("Use RetrieveTrackingNumberRange(string accountNumber, string meterNumber, Shipment.CarrierType carrierType, bool currentCode) instead.")]
    public TrackingNumberResponse RetrieveTrackingNumberRange(
      string accountNumber,
      string meterNumber,
      Shipment.CarrierType carrierType)
    {
      try
      {
        return ShipEngineComponents.ShipEngine.RetrieveTrackingNumberRange(accountNumber, meterNumber, carrierType);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new TrackingNumberResponse(new Error(4));
      }
    }

    public TrackingNumberResponse RetrieveTrackingNumberRange(
      string accountNumber,
      string meterNumber,
      Shipment.CarrierType carrierType,
      bool currentCode)
    {
      try
      {
        return ShipEngineComponents.ShipEngine.RetrieveTrackingNumberRange(accountNumber, meterNumber, carrierType, currentCode);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new TrackingNumberResponse(new Error(4));
      }
    }

    public ServiceResponse SetupAccountMeter(Account newAccountMeter)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.SetupAccountMeter(newAccountMeter);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ModifyAccountMeter(Account newAccountMeter)
    {
      try
      {
        return ShipEngineComponents.ShipEngine.ModifyAccountMeter(newAccountMeter);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse UpdateTrackingNumberRange(TrackingNumber trackingNumberRange)
    {
      try
      {
        return ShipEngineComponents.ShipEngine.UpdateTrackingNumberRange(trackingNumberRange);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public SvcListResponse GetServiceList(Shipment shipment)
    {
      try
      {
        if (ShipEngineCommonUtilities.IsEngineOnly())
        {
          Error error = new Error();
          Account account = ShipEngineComponents.DataAccessService.GetAccount(shipment.Account.AccountNumber, shipment.Account.MeterNumber, error);
          if (account != null)
            shipment.Account = new Account(account);
        }
        return ShipEngineComponents.ShipEngine.GetServiceList(shipment);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new SvcListResponse(new SvcList(), new Error(4));
      }
    }

    public SvcListResponse GetServiceList(Account account, FieldPref.PreferenceTypes preferenceType)
    {
      try
      {
        return ShipEngineComponents.ShipEngine.GetServiceList(account, preferenceType);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new SvcListResponse(new SvcList(), new Error(4));
      }
    }

    public SvcListResponse GetServiceList(
      Account account,
      FieldPref.PreferenceTypes preferenceType,
      Recipient recipient)
    {
      try
      {
        return ShipEngineComponents.ShipEngine.GetServiceList(account, preferenceType, recipient);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new SvcListResponse(new SvcList(), new Error(4));
      }
    }

    public PickupCancelResponse CancelPickupDispatch(PickupCancelRequest pickupCancelRequest)
    {
      try
      {
        return ShipEngineComponents.PickupServiceComponent.CancelPickupDispatch(pickupCancelRequest);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        PickupCancelResponse pickupCancelResponse = new PickupCancelResponse();
        pickupCancelResponse.Error = new Error(4);
        return pickupCancelResponse;
      }
    }

    public PickupResponse SubmitPickupDispatch(PickupRequest pickupRequest)
    {
      try
      {
        return ShipEngineComponents.PickupServiceComponent.SubmitPickupDispatch(pickupRequest);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        PickupResponse pickupResponse = new PickupResponse();
        pickupResponse.Error = new Error(4);
        return pickupResponse;
      }
    }

    public bool SubmitPickup(PickupInfo inPickupInfo, out PickupInfo outPickupInfo, Error puError)
    {
      try
      {
        return ShipEngineComponents.PickupServiceComponent.SubmitPickup(inPickupInfo, out outPickupInfo, puError);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        outPickupInfo = new PickupInfo();
        return false;
      }
    }

    public bool CancelPickup(PickupInfo pi, Error error)
    {
      try
      {
        return ShipEngineComponents.PickupServiceComponent.CancelPickup(pi, error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return false;
      }
    }

    public bool DeletePickupDispatch(string pickupId)
    {
      try
      {
        return ShipEngineComponents.PickupServiceComponent.DeletePickupDispatch(pickupId);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return false;
      }
    }

    public PickupInfoList GetPickupHistory(string meter, string account, Error error)
    {
      try
      {
        return ShipEngineComponents.PickupServiceComponent.GetPickupHistory(meter, account, error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new PickupInfoList();
      }
    }

    public PickupInfo GetPickupInfoForPickupId(string pickupId, Error error)
    {
      try
      {
        return ShipEngineComponents.PickupServiceComponent.GetPickupInfoForPickupId(pickupId, error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new PickupInfo();
      }
    }

    public PickupInfoResponse SubmitPickup(PickupInfo inPickupInfo)
    {
      try
      {
        Error error = new Error();
        PickupInfo outPickupInfo;
        ShipEngineComponents.PickupServiceComponent.SubmitPickup(inPickupInfo, out outPickupInfo, error);
        return new PickupInfoResponse(outPickupInfo, error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new PickupInfoResponse(new PickupInfo(), new Error(4));
      }
    }

    public ServiceResponse CancelPickup(PickupInfo pi)
    {
      try
      {
        Error error = new Error();
        ShipEngineComponents.PickupServiceComponent.CancelPickup(pi, error);
        return new ServiceResponse(error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public PickupInfoListResponse GetPickupHistory(string meter, string account)
    {
      try
      {
        Error error = new Error();
        return new PickupInfoListResponse(ShipEngineComponents.PickupServiceComponent.GetPickupHistory(meter, account, error), error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new PickupInfoListResponse(new PickupInfoList(), new Error(4));
      }
    }

    public PickupInfoResponse GetPickupInfoForPickupId(string pickupId)
    {
      try
      {
        Error error = new Error();
        return new PickupInfoResponse(ShipEngineComponents.PickupServiceComponent.GetPickupInfoForPickupId(pickupId, error), error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new PickupInfoResponse(new PickupInfo(), new Error(4));
      }
    }

    public TrackServiceResponse GetTrackingData(TrackServiceRequest request)
    {
      try
      {
        return ShipEngineComponents.TrackServiceComponent.GetTrackingData(request);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new TrackServiceResponse(new List<TrackReplyBase>(), new Error(4));
      }
    }

    public RevenueServiceResponse CheckCloseStatus(RevenueServiceRequest revenueServiceRequest)
    {
      try
      {
        return ShipEngineComponents.RevService.CheckCloseStatus(revenueServiceRequest);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new RevenueServiceResponse(new Error(4));
      }
    }

    public RevenueServiceResponse CloseMeter(RevenueServiceRequest revenueServiceRequest)
    {
      try
      {
        return ShipEngineComponents.RevService.CloseMeter(revenueServiceRequest);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new RevenueServiceResponse(new Error(4));
      }
    }

    public RevenueServiceResponse GetTheNumberOfFilesToBeUploaded(
      RevenueServiceRequest revenueServiceRequest)
    {
      try
      {
        return ShipEngineComponents.RevService.GetTheNumberOfFilesToBeUploaded(revenueServiceRequest);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new RevenueServiceResponse(new Error(4));
      }
    }

    public RevenueServiceResponse DemandUpload(RevenueServiceRequest revenueServiceRequest)
    {
      try
      {
        return ShipEngineComponents.RevService.DemandUpload(revenueServiceRequest);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new RevenueServiceResponse(new Error(4));
      }
    }

    public RevenueServiceResponse CheckDemandUploadStatus(
      RevenueServiceRequest revenueServiceRequest)
    {
      try
      {
        return ShipEngineComponents.RevService.CheckDemandUploadStatus(revenueServiceRequest);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new RevenueServiceResponse(new Error(4));
      }
    }

    public RevenueServiceResponse CopyFilesFromUploadDirectory(string DestinationDirectory)
    {
      try
      {
        return ShipEngineComponents.RevService.CopyFilesFromUploadDirectory(DestinationDirectory);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new RevenueServiceResponse(new Error(4));
      }
    }

    public RevenueServiceResponse CopyFilesToUploadDirectory(string OriginDirectory)
    {
      try
      {
        return ShipEngineComponents.RevService.CopyFilesToUploadDirectory(OriginDirectory);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new RevenueServiceResponse(new Error(4));
      }
    }

    public RevenueServiceResponse DelayCloseTIme(Account meterToDelay, int minutesToDelay)
    {
      try
      {
        return ShipEngineComponents.RevService.DelayCloseTIme(meterToDelay, minutesToDelay);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new RevenueServiceResponse(new Error(4));
      }
    }

    public RevenueServiceResponse GetAutoCloseTime(Account meterToInquire)
    {
      try
      {
        return ShipEngineComponents.RevService.GetAutoCloseTime(meterToInquire);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new RevenueServiceResponse(new Error(4));
      }
    }

    public RevenueServiceResponse GetUploadFilePath()
    {
      try
      {
        return ShipEngineComponents.RevService.GetUploadFilePath();
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new RevenueServiceResponse(new Error(4));
      }
    }

    public RevenueServiceResponse GetUploadStatus(Account account, string cycleNumber)
    {
      try
      {
        return ShipEngineComponents.RevService.GetUploadStatus(account, cycleNumber);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new RevenueServiceResponse(new Error(4));
      }
    }

    public RevenueServiceResponse GetETDUploadStatus(
      Account account,
      List<string> TrackingNumbersList)
    {
      try
      {
        return ShipEngineComponents.RevService.GetETDUploadStatus(account, TrackingNumbersList);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new RevenueServiceResponse(new Error(4));
      }
    }

    public RevenueServiceResponse RetrieveShipmentCounts(
      Account meterToCheck,
      Shipment.CarrierType carrierType,
      CloseInfo.closeType closeType)
    {
      try
      {
        return ShipEngineComponents.RevService.RetrieveShipmentCounts(meterToCheck, carrierType, closeType);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new RevenueServiceResponse(new Error(4));
      }
    }

    public RevenueServiceResponse RetrieveShipmentCounts(
      Account meterToCheck,
      Shipment.CarrierType carrierType)
    {
      try
      {
        return ShipEngineComponents.RevService.RetrieveShipmentCounts(meterToCheck, carrierType);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new RevenueServiceResponse(new Error(4));
      }
    }

    public RevenueServiceResponse RestartRevService(RevenueServiceRequest revenueServiceRequest)
    {
      try
      {
        return ShipEngineComponents.RevService.RestartRevService(revenueServiceRequest);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new RevenueServiceResponse(new Error(4));
      }
    }

    public ShipmentResponse UploadETD(Shipment shipment)
    {
      try
      {
        return ShipEngineComponents.RevService.UploadETD(shipment);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ShipmentResponse(new Error(4));
      }
    }

    public ServiceResponse RestartRateAndRouteServices(
      string accountNumber,
      string meterNumber,
      string rateType,
      AdminDownloadRequest.RequestType type)
    {
      try
      {
        FxLogger.LogMessage(FedEx.Gsm.Common.Logging.LogLevel.Info, FxLogger.AppCode_Default, "RestartRateAndRouteServices()", "Entered");
        string inMessage = "With Acct:" + accountNumber + " ,Meter:" + meterNumber + " ,RateType:" + rateType + " ,AdminRequestType:" + type.ToString();
        FedEx.Gsm.Common.ConfigManager.ConfigManager configManager = new FedEx.Gsm.Common.ConfigManager.ConfigManager();
        switch (type)
        {
          case AdminDownloadRequest.RequestType.DomesticRates:
            ServiceResponse serviceResponse1 = new ServiceResponse();
            if (configManager.IsEngineOnly)
              ShipEngineComponents.DisposeRateService();
            FxLogger.LogMessage(FedEx.Gsm.Common.Logging.LogLevel.Info, FxLogger.AppCode_Default, "Calling RateService.InitializeRating()", inMessage);
            if (!configManager.IsEngineOnly)
              return ShipEngineComponents.RateService.InitializeRating(accountNumber, meterNumber, rateType);
            ServiceResponse serviceResponse2 = ShipEngineComponents.RateService.InitializeRating(accountNumber, meterNumber, rateType);
            if (serviceResponse2.ErrorCode != 1)
              ShipEngineComponents.EditService.LoadPolicyGrid();
            return serviceResponse2;
          case AdminDownloadRequest.RequestType.GroundRates:
            FxLogger.LogMessage(FedEx.Gsm.Common.Logging.LogLevel.Info, FxLogger.AppCode_Default, nameof (RestartRateAndRouteServices), "Calling RateService.ImportGroundRates()");
            return ShipEngineComponents.RateService.ImportGroundRates("");
          case AdminDownloadRequest.RequestType.Ursa_Generic:
            FxLogger.LogMessage(FedEx.Gsm.Common.Logging.LogLevel.Info, FxLogger.AppCode_Default, nameof (RestartRateAndRouteServices), "Calling EditService.InitializeRoute()");
            return ShipEngineComponents.EditService.InitializeRoute();
          case AdminDownloadRequest.RequestType.ESRG:
            FxLogger.LogMessage(FedEx.Gsm.Common.Logging.LogLevel.Info, FxLogger.AppCode_Default, nameof (RestartRateAndRouteServices), "Calling RouteService.InitializeESRG()");
            ShipEngineComponents.RouteService.InitializeESRG();
            return new ServiceResponse(1);
          case AdminDownloadRequest.RequestType.GroundTransitFile:
            FxLogger.LogMessage(FedEx.Gsm.Common.Logging.LogLevel.Info, FxLogger.AppCode_Default, nameof (RestartRateAndRouteServices), "Calling EditService.InitializeSasvData()");
            return ShipEngineComponents.EditService.InitializeSasvData();
          case AdminDownloadRequest.RequestType.PolicyGrid:
            FxLogger.LogMessage(FedEx.Gsm.Common.Logging.LogLevel.Info, FxLogger.AppCode_Default, nameof (RestartRateAndRouteServices), "Calling EditService.LoadPolicyGridData()");
            ShipEngineComponents.EditService.LoadPolicyGrid();
            return new ServiceResponse(1);
          case AdminDownloadRequest.RequestType.ProductBrandMaster:
            FxLogger.LogMessage(FedEx.Gsm.Common.Logging.LogLevel.Info, FxLogger.AppCode_Default, nameof (RestartRateAndRouteServices), "Calling ShiServiceImp.RefreshProductBrandMaster");
            ShipEngineComponents.ShipEngine.RefreshProductBrandMaster();
            return new ServiceResponse(1);
          case AdminDownloadRequest.RequestType.ClearanceInformation:
            FxLogger.LogMessage(FedEx.Gsm.Common.Logging.LogLevel.Info, FxLogger.AppCode_Default, nameof (RestartRateAndRouteServices), "Calling EditService.RefreshClearanceFacilitiesData()");
            ShipEngineComponents.EditService.RefreshClearanceFacilitiesData();
            return new ServiceResponse(1);
          default:
            return new ServiceResponse();
        }
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public bool IsUploadOverdue()
    {
      try
      {
        return ShipEngineComponents.RevService.IsUploadOverdue();
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return false;
      }
    }

    public bool isRevengineRunning()
    {
      try
      {
        return ShipEngineComponents.RevService != null && ShipEngineComponents.RevService.isRevengineRunning();
      }
      catch (Exception ex)
      {
        return false;
      }
    }

    public bool beginPSDUforGSM(RevenueServiceRequest inGSMRequest)
    {
      try
      {
        return ShipEngineComponents.RevService.beginPSDUforGSM(inGSMRequest);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return false;
      }
    }

    public FreightShipmentResponse ProcessFreightShipment(
      FreightShipmentRequest freightShipmentRequest)
    {
      try
      {
        return ShipEngineComponents.FreightIntegrationService.ProcessFreightShipment(freightShipmentRequest);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new FreightShipmentResponse(new Error(4));
      }
    }

    public ScheduleFreightShipmentPickupResponse ScheduleFreightShipmentPickup(
      ScheduleFreightShipmentPickupRequest schedulePickupRequest)
    {
      try
      {
        return ShipEngineComponents.FreightIntegrationService.ScheduleFreightShipmentPickup(schedulePickupRequest);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ScheduleFreightShipmentPickupResponse(new Error(4));
      }
    }

    public RateFreightShipmentResponse RateFreightShipment(
      RateFreightShipmentRequest rateFreightShipmentRequest)
    {
      try
      {
        RateFreightShipmentResponse shipmentResponse = ShipEngineComponents.FreightIntegrationService.RateFreightShipment(rateFreightShipmentRequest);
        if (!shipmentResponse.HasError)
        {
          Error error = new Error();
          ShipEngineCommonUtilities.LogAnalytics(rateFreightShipmentRequest.FreightShipment, AnalyticsLog.LoggingType.FreightRateQuoteRequest, 1, error);
        }
        return shipmentResponse;
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new RateFreightShipmentResponse(new Error(4));
      }
    }

    public ServiceResponse DeleteFreightShipment(string freightAcctNbr, string shipmentId)
    {
      try
      {
        return ShipEngineComponents.FreightIntegrationService.DeleteFreightShipment(freightAcctNbr, shipmentId);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ModifyFreightShipment(FreightShipment freightShipment)
    {
      try
      {
        return ShipEngineComponents.FreightIntegrationService.ModifyFreightShipment(freightShipment);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public FreightShipmentResponse GetFreightImageToPrint(
      string freightAcctNbr,
      string shipmentId,
      ShipmentDocument.DocumentType docType)
    {
      try
      {
        return ShipEngineComponents.FreightIntegrationService.GetFreightImageToPrint(freightAcctNbr, shipmentId, docType);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new FreightShipmentResponse(new Error(4));
      }
    }

    public ServiceResponse ValidateLTLFreightAddress(FreightBillFreightChargeTo address)
    {
      try
      {
        return ShipEngineComponents.FreightIntegrationService.ValidateLTLFreightAddress(address);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public void SetCultureInfoforAdvisoryfiles(CultureInfo cult)
    {
      try
      {
        ShipEngineComponents.EditService.LoadAdvisoryFiles(cult.Name);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
      }
    }

    public List<string> GetNaftaRoleList(Address origin, Address destination)
    {
      return ShipEngineComponents.EditService.GetNaftaRoleList(origin, destination);
    }

    public ServiceResponse ValidateCommercialInvoice(CommercialInvoice commercialInvoice)
    {
      return ShipEngineComponents.EditService.ValidateCommercialInvoice(commercialInvoice);
    }

    public ServiceResponse ValidateCommercialInvoice(Shipment shipment)
    {
      return ShipEngineComponents.EditService.ValidateCommercialInvoice(shipment);
    }

    public List<string> GetNaftaRoleList() => ShipEngineComponents.EditService.GetNaftaRoleList();

    public GroundTransitTimeResponse GetGroundTransitTime(GroundTransitTimeRequest request)
    {
      return ShipEngineComponents.EditService.GetGroundTransitTime(request);
    }

    public PolicyResponse ValidatePolicyGrid(PolicyRequest request)
    {
      return ShipEngineComponents.EditService.ValidatePolicyGrid(request);
    }

    public bool IsPSDUAllowed(Shipment shipment)
    {
      return ShipEngineComponents.EditService.IsPSDUAllowed(shipment);
    }

    public ServiceResponse ValidateThirdPartyAccount(ThirdPartyAccount account)
    {
      return ShipEngineComponents.EditService.ValidateThirdPartyAccount(account);
    }

    public Error ValidatePieceCountVerification(
      Sender sender,
      Recipient recipient,
      IDFPieceCntVerify pieceCount)
    {
      return ShipEngineComponents.EditService.ValidatePieceCountVerification(sender, recipient, pieceCount);
    }

    public Error ValidateSpecialServiceCombination(Shipment shipment)
    {
      Error error = new Error();
      return ShipEngineComponents.EditService.ValidateSpecialServiceCombination(shipment);
    }

    public ServiceResponse ProcessDataIntegrationTrans(
      TransactionData request,
      out TransactionData reply)
    {
      try
      {
        TransactionParser trxnParserOut;
        ServiceResponse serviceResponse = ThreeFiftyTransactionTrafficCop.ProcessTransaction(new TransactionParser(request), out trxnParserOut);
        reply = new TransactionData(trxnParserOut);
        return serviceResponse;
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        reply = new TransactionData();
        return new ServiceResponse(new Error(4));
      }
    }

    public DataIntegrationResponse ProcessDataIntegrationRequest(DataIntegrationRequest request)
    {
      try
      {
        return ThreeFiftyTransactionTrafficCop.ProcessDataIntegrationRequest(request);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new DataIntegrationResponse(new Error(4));
      }
    }

    public ServiceResponse RegisterCommStatusSubscriber(Delegate subscriber)
    {
      return ShipEngineComponents.CommunicationStatus.InitCommStatusMessages(subscriber);
    }

    public ServiceResponse CancelCommSession()
    {
      return ShipEngineComponents.CommunicationStatus.CancelCommSession();
    }

    public bool URSAGroundSupported(DateTime date)
    {
      bool flag = true;
      try
      {
        flag = ShipEngineComponents.RouteService.URSAGroundSupported(date);
      }
      catch (Exception ex)
      {
        FxLogger.LogMessage(FedEx.Gsm.Common.Logging.LogLevel.Error, FxLogger.AppCode_Default, nameof (URSAGroundSupported), ex.Message);
      }
      return flag;
    }

    public Error QASInitialize(Account account, ref int sessionID)
    {
      return new FedEx.Gsm.ShipEngine.External.QASWrapper.QASWrapper().QASInitialize(account, ref sessionID);
    }

    public QASAddressResponse QASStepIn(int index, int sessionID)
    {
      return new FedEx.Gsm.ShipEngine.External.QASWrapper.QASWrapper().QASStepIn(index, sessionID);
    }

    public QASAddressResponse QASStepOut(int index, int sessionID)
    {
      return new FedEx.Gsm.ShipEngine.External.QASWrapper.QASWrapper().QASStepOut(index, sessionID);
    }

    public QASAddressResponse QASStartSearch(string queryString, int sessionID)
    {
      return new FedEx.Gsm.ShipEngine.External.QASWrapper.QASWrapper().QASStartSearch(queryString, sessionID);
    }

    public bool QASIsFinalAddress(int index, int sessionID)
    {
      return new FedEx.Gsm.ShipEngine.External.QASWrapper.QASWrapper().QASIsFinalAddress(index, sessionID);
    }

    public Error QASGetFinalAddress(int index, ref Address address, int sessionID)
    {
      return address == null ? new Error(0) : new FedEx.Gsm.ShipEngine.External.QASWrapper.QASWrapper().QASGetFinalAddress(index, ref address, sessionID);
    }

    public QASInformation QASStatus(int sessionID) => new FedEx.Gsm.ShipEngine.External.QASWrapper.QASWrapper().QASStatus(sessionID);

    public Error QASValidateAddress(Account account, Address address)
    {
      return new FedEx.Gsm.ShipEngine.External.QASWrapper.QASWrapper().QASValidateAddress(account, address);
    }

    public List<SplSvc> GetSupportedSpecialServices(
      string productId,
      string langCd,
      ref List<SplSvc> newSplList,
      out Error error)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.GetSupportedSpecialServices(productId, langCd, ref newSplList, out error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        error = new Error(4);
        return new List<SplSvc>();
      }
    }

    public ServiceResponse MidnightPeep()
    {
      ServiceResponse serviceResponse = new ServiceResponse();
      serviceResponse.Error = new Error();
      try
      {
        return ShipEngineComponents.RevService != null ? ShipEngineComponents.RevService.MidnightPeep() : serviceResponse;
      }
      catch (Exception ex)
      {
        serviceResponse.Error.Code = -1;
        return serviceResponse;
      }
    }

    public ServiceResponse GetSupportedRateComponents(ref List<rcdDataComp> lstSupportDoc)
    {
      try
      {
        return ShipEngineComponents.RateService.GetSupportedRateComponents(ref lstSupportDoc);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse GetSupportedRateComponents(
      ref List<rcdDataComp> lstSupportDoc,
      string countryCode)
    {
      try
      {
        return ShipEngineComponents.RateService.GetSupportedRateComponents(ref lstSupportDoc, countryCode);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ImplementRateDataComponents(
      string account,
      string meter,
      string componentId,
      string fileName)
    {
      try
      {
        return ShipEngineComponents.RateService.ImplementRateDataComponents(account, meter, componentId, fileName);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public bool ClearAllAddress3(out Error error)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.ClearAllAddress3(out error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        error = new Error(-1, ex.Message);
        return false;
      }
    }

    public bool CheckAnyAddress3(out Error error)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.CheckAnyAddress3(out error);
      }
      catch (Exception ex)
      {
        error = new Error(-1, ex.Message);
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return false;
      }
    }

    public Error ValidateUSMCACertifiedRoleInfo(Address address, NaftaCO.CertifierType roleType)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidateUSMCACertifiedRoleInfo(address, roleType);
      }
      catch (Exception ex)
      {
        return new Error(-1, ex.Message);
      }
    }

    public Error ValidateUSMCACertifiedRoleInfo(
      Address address,
      NaftaCO.CertifierType roleType,
      InputSourceType source)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidateUSMCACertifiedRoleInfo(address, roleType, source);
      }
      catch (Exception ex)
      {
        return new Error(-1, ex.Message);
      }
    }

    public ServiceResponse ValidateIDDDropOffLocation(IDDDropOff dropOff)
    {
      try
      {
        return ShipEngineComponents.EditService.ValidateIDDDropOffLocation(dropOff);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public DataSetResponse GetRecipientDataSetList(
      Recipient filter,
      FedEx.Gsm.ShipEngine.Entities.ListSpecification listType,
      List<GsmFilter> filterList,
      List<GsmSort> sortList,
      List<string> columnNames)
    {
      try
      {
        Error error = new Error();
        DataSet output;
        ShipEngineComponents.DataAccessService.GetDataList((object) filter, listType, out output, filterList, sortList, columnNames, error);
        if (output != null)
          output.RemotingFormat = SerializationFormat.Binary;
        return new DataSetResponse(output, error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new DataSetResponse(new DataSet(), new Error(4));
      }
    }

    public DataTableResponse GetSvcBullBrdDataList(SvcBullBrd filter, FedEx.Gsm.ShipEngine.Entities.ListSpecification listType)
    {
      try
      {
        Error error = new Error();
        DataTable output;
        ShipEngineComponents.DataAccessService.GetDataList((object) filter, listType, out output, error);
        if (output != null)
          output.RemotingFormat = SerializationFormat.Binary;
        return new DataTableResponse(output, error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        DataTableResponse svcBullBrdDataList = new DataTableResponse();
        svcBullBrdDataList.Error = new Error(4);
        return svcBullBrdDataList;
      }
    }

    public DataTableResponse GetWSEDDataList(
      WSEDInfo filter,
      FedEx.Gsm.ShipEngine.Entities.ListSpecification listType,
      List<GsmFilter> filterList,
      List<GsmSort> sortList,
      List<string> columnNames)
    {
      try
      {
        Error error = new Error();
        DataTable output;
        ShipEngineComponents.DataAccessService.GetDataList((object) filter, listType, out output, filterList, sortList, columnNames, error);
        if (output != null)
          output.RemotingFormat = SerializationFormat.Binary;
        return new DataTableResponse(output, error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        DataTableResponse wsedDataList = new DataTableResponse();
        wsedDataList.Error = new Error(4);
        return wsedDataList;
      }
    }

    public DataSetResponse GetIDDThirdPartyDataSetList(
      IDD3rdPartyShip filter,
      FedEx.Gsm.ShipEngine.Entities.ListSpecification listType,
      List<GsmFilter> filterList,
      List<GsmSort> sortList,
      List<string> columnNames)
    {
      try
      {
        Error error = new Error();
        DataSet output;
        ShipEngineComponents.DataAccessService.GetDataList((object) filter, listType, out output, filterList, sortList, columnNames, error);
        if (output != null)
          output.RemotingFormat = SerializationFormat.Binary;
        return new DataSetResponse(output, error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new DataSetResponse(new DataSet(), new Error(4));
      }
    }

    public DataTableResponse GetRecipientGroupDataList(
      RecipientGroup filter,
      FedEx.Gsm.ShipEngine.Entities.ListSpecification listType)
    {
      try
      {
        Error error = new Error();
        DataTable output;
        ShipEngineComponents.DataAccessService.GetDataList((object) filter, listType, out output, error);
        if (output != null)
          output.RemotingFormat = SerializationFormat.Binary;
        return new DataTableResponse(output, error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        DataTableResponse recipientGroupDataList = new DataTableResponse();
        recipientGroupDataList.Error = new Error(4);
        return recipientGroupDataList;
      }
    }

    public DataTableResponse GetTrkngNbrQryListDataList(
      TrkngNbrQryList filter,
      FedEx.Gsm.ShipEngine.Entities.ListSpecification listType)
    {
      try
      {
        Error error = new Error();
        DataTable output;
        ShipEngineComponents.DataAccessService.GetDataList((object) filter, listType, out output, error);
        if (output != null)
          output.RemotingFormat = SerializationFormat.Binary;
        return new DataTableResponse(output, error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        DataTableResponse nbrQryListDataList = new DataTableResponse();
        nbrQryListDataList.Error = new Error(4);
        return nbrQryListDataList;
      }
    }

    public int Retrieve(Account filter, out Account output, out Error error)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Retrieve<Account>(filter, out output, out error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        output = (Account) null;
        error = new Error(4);
        return 4;
      }
    }

    public int Retrieve(DefaultDShipDefl filter, out DefaultDShipDefl output, out Error error)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Retrieve<DefaultDShipDefl>(filter, out output, out error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        output = (DefaultDShipDefl) null;
        error = new Error(4);
        return 4;
      }
    }

    public int Retrieve(DefaultIShipDefl filter, out DefaultIShipDefl output, out Error error)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Retrieve<DefaultIShipDefl>(filter, out output, out error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        output = (DefaultIShipDefl) null;
        error = new Error(4);
        return 4;
      }
    }

    public int Retrieve(DefaultFShipDefl filter, out DefaultFShipDefl output, out Error error)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Retrieve<DefaultFShipDefl>(filter, out output, out error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        output = (DefaultFShipDefl) null;
        error = new Error(4);
        return 4;
      }
    }

    public int Retrieve(DefaultTDShipDefl filter, out DefaultTDShipDefl output, out Error error)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Retrieve<DefaultTDShipDefl>(filter, out output, out error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        output = (DefaultTDShipDefl) null;
        error = new Error(4);
        return 4;
      }
    }

    public int Retrieve(FormSetting filter, out FormSetting output, out Error error)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Retrieve<FormSetting>(filter, out output, out error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        output = (FormSetting) null;
        error = new Error(4);
        return 4;
      }
    }

    public int Retrieve(Sender filter, out Sender output, out Error error)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Retrieve<Sender>(filter, out output, out error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        output = (Sender) null;
        error = new Error(4);
        return 4;
      }
    }

    public int Retrieve(SystemPrefs filter, out SystemPrefs output, out Error error)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Retrieve<SystemPrefs>(filter, out output, out error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        output = (SystemPrefs) null;
        error = new Error(4);
        return 4;
      }
    }

    public int Retrieve(DShipDefl filter, out DShipDefl output, out Error error)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Retrieve<DShipDefl>(filter, out output, out error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        output = (DShipDefl) null;
        error = new Error(4);
        return 4;
      }
    }

    public int Retrieve(IShipDefl filter, out IShipDefl output, out Error error)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Retrieve<IShipDefl>(filter, out output, out error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        output = (IShipDefl) null;
        error = new Error(4);
        return 4;
      }
    }

    public int Retrieve(FShipDefl filter, out FShipDefl output, out Error error)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Retrieve<FShipDefl>(filter, out output, out error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        output = (FShipDefl) null;
        error = new Error(4);
        return 4;
      }
    }

    public int Retrieve(TDShipDefl filter, out TDShipDefl output, out Error error)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Retrieve<TDShipDefl>(filter, out output, out error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        output = (TDShipDefl) null;
        error = new Error(4);
        return 4;
      }
    }

    public int Retrieve(MessageQueueEntry filter, out MessageQueueEntry output, out Error error)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Retrieve<MessageQueueEntry>(filter, out output, out error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        output = (MessageQueueEntry) null;
        error = new Error(4);
        return 4;
      }
    }

    public int Retrieve(Recipient filter, out Recipient output, out Error error)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Retrieve<Recipient>(filter, out output, out error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        output = (Recipient) null;
        error = new Error(4);
        return 4;
      }
    }

    public ServiceResponse ModifyAccount(Account input)
    {
      try
      {
        return this.Modify((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse InsertSenderRequest(SenderRequest input)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Insert((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ModifySenderRequest(SenderRequest input)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Modify((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse InsertRecipientRequest(RecipientRequest input)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Insert((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ModifyRecipientRequest(RecipientRequest input)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Modify((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse InsertCommodityRequest(CommodityRequest input)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Insert((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ModifyCommodityRequest(CommodityRequest input)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Modify((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse InsertAccount(Account input)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Insert((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse DeleteAccount(Account input)
    {
      try
      {
        return this.Delete((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse InsertRecipient(Recipient input)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Insert((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ModifyRecipient(Recipient input)
    {
      try
      {
        return this.Modify((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse DeleteRecipient(Recipient input)
    {
      try
      {
        return this.Delete((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ModifyFormSetting(FormSetting input)
    {
      try
      {
        return this.Modify((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse InsertFormSetting(FormSetting input)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Insert((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse DeleteFormSetting(FormSetting input)
    {
      try
      {
        return this.Delete((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ModifySvcBullBrd(SvcBullBrd input)
    {
      try
      {
        return this.Modify((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse InsertSvcBullBrd(SvcBullBrd input)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Insert((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse DeleteSvcBullBrd(SvcBullBrd input)
    {
      try
      {
        return this.Delete((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ModifySender(Sender input)
    {
      try
      {
        return this.Modify((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse InsertSender(Sender input)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Insert((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse DeleteSender(Sender input)
    {
      try
      {
        return this.Delete((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public int Retrieve(Broker filter, out Broker output, out Error error)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Retrieve<Broker>(filter, out output, out error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        output = (Broker) null;
        error = new Error(4);
        return 4;
      }
    }

    public ServiceResponse ModifyBroker(Broker input)
    {
      try
      {
        return this.Modify((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse InsertBroker(Broker input)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Insert((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse DeleteBroker(Broker input)
    {
      try
      {
        return this.Delete((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public int Retrieve(Shipment filter, out Shipment output, out Error error)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Retrieve<Shipment>(filter, out output, out error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        output = (Shipment) null;
        error = new Error(4);
        return 4;
      }
    }

    public ServiceResponse DeleteShipment(Shipment input)
    {
      try
      {
        return this.Delete((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public int Retrieve(Commodity filter, out Commodity output, out Error error)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Retrieve<Commodity>(filter, out output, out error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        output = (Commodity) null;
        error = new Error(4);
        return 4;
      }
    }

    public ServiceResponse ModifyCommodity(Commodity input)
    {
      try
      {
        return this.Modify((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse InsertCommodity(Commodity input)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Insert((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse DeleteCommodity(Commodity input)
    {
      try
      {
        return this.Delete((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public int Retrieve(SFCTemplate filter, out SFCTemplate output, out Error error)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Retrieve<SFCTemplate>(filter, out output, out error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        output = (SFCTemplate) null;
        error = new Error(4);
        return 4;
      }
    }

    public ServiceResponse ModifySFCTemplate(SFCTemplate input)
    {
      try
      {
        return this.Modify((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse InsertSFCTemplate(SFCTemplate input)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Insert((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse DeleteSFCTemplate(SFCTemplate input)
    {
      try
      {
        return this.Delete((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public int Retrieve(LTLFreightTemplate filter, out LTLFreightTemplate output, out Error error)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Retrieve<LTLFreightTemplate>(filter, out output, out error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        output = (LTLFreightTemplate) null;
        error = new Error(4);
        return 4;
      }
    }

    public ServiceResponse ModifyLTLFreightTemplate(LTLFreightTemplate input)
    {
      try
      {
        return this.Modify((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse InsertLTLFreightTemplate(LTLFreightTemplate input)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Insert((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse DeleteLTLFreightTemplate(LTLFreightTemplate input)
    {
      try
      {
        return this.Delete((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public int Retrieve(CloseInfo filter, out CloseInfo output, out Error error)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Retrieve<CloseInfo>(filter, out output, out error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        output = (CloseInfo) null;
        error = new Error(4);
        return 4;
      }
    }

    public ServiceResponse ModifyCloseInfo(CloseInfo input)
    {
      try
      {
        return this.Modify((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public int Retrieve(User filter, out User output, out Error error)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Retrieve<User>(filter, out output, out error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        output = (User) null;
        error = new Error(4);
        return 4;
      }
    }

    public ServiceResponse ModifyUser(User input)
    {
      try
      {
        return this.Modify((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse InsertUser(User input)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Insert((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse DeleteUser(User input)
    {
      try
      {
        return this.Delete((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ModifySystemPrefs(SystemPrefs input)
    {
      try
      {
        return this.Modify((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse InsertSystemPrefs(SystemPrefs input)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Insert((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse DeleteSystemPrefs(SystemPrefs input)
    {
      try
      {
        return this.Delete((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public int Retrieve(Reference filter, out Reference output, out Error error)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Retrieve<Reference>(filter, out output, out error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        output = (Reference) null;
        error = new Error(4);
        return 4;
      }
    }

    public ServiceResponse ModifyReference(Reference input)
    {
      try
      {
        return this.Modify((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse InsertReference(Reference input)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Insert((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse DeleteReference(Reference input)
    {
      try
      {
        return this.Delete((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public int Retrieve(IPDIORRequest filter, out IPDIORRequest output, out Error error)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Retrieve<IPDIORRequest>(filter, out output, out error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        output = (IPDIORRequest) null;
        error = new Error(4);
        return 4;
      }
    }

    public ServiceResponse ModifyIPDIORRequest(IPDIORRequest input)
    {
      try
      {
        return this.Modify((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse InsertIPDIORRequest(IPDIORRequest input)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Insert((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse DeleteIPDIORRequest(IPDIORRequest input)
    {
      try
      {
        return this.Delete((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public int Retrieve(RecipientGroup filter, out RecipientGroup output, out Error error)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Retrieve<RecipientGroup>(filter, out output, out error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        output = (RecipientGroup) null;
        error = new Error(4);
        return 4;
      }
    }

    public ServiceResponse ModifyRecipientGroup(RecipientGroup input)
    {
      try
      {
        return this.Modify((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse InsertRecipientGroup(RecipientGroup input)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Insert((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse DeleteRecipientGroup(RecipientGroup input)
    {
      try
      {
        return this.Delete((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public int Retrieve(Dimension filter, out Dimension output, out Error error)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Retrieve<Dimension>(filter, out output, out error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        output = (Dimension) null;
        error = new Error(4);
        return 4;
      }
    }

    public ServiceResponse ModifyDimension(Dimension input)
    {
      try
      {
        return this.Modify((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse InsertDimension(Dimension input)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Insert((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse DeleteDimension(Dimension input)
    {
      try
      {
        return this.Delete((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public int Retrieve(Department filter, out Department output, out Error error)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Retrieve<Department>(filter, out output, out error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        output = (Department) null;
        error = new Error(4);
        return 4;
      }
    }

    public ServiceResponse ModifyDepartment(Department input)
    {
      try
      {
        return this.Modify((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse InsertDepartment(Department input)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Insert((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse DeleteDepartment(Department input)
    {
      try
      {
        return this.Delete((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public int Retrieve(FreightAccount filter, out FreightAccount output, out Error error)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Retrieve<FreightAccount>(filter, out output, out error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        output = (FreightAccount) null;
        error = new Error(4);
        return 4;
      }
    }

    public ServiceResponse ModifyFreightAccount(FreightAccount input)
    {
      try
      {
        return this.Modify((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse InsertFreightAccount(FreightAccount input)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Insert((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse DeleteFreightAccount(FreightAccount input)
    {
      try
      {
        return this.Delete((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public int Retrieve(HoldFileInfo filter, out HoldFileInfo output, out Error error)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Retrieve<HoldFileInfo>(filter, out output, out error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        output = (HoldFileInfo) null;
        error = new Error(4);
        return 4;
      }
    }

    public ServiceResponse ModifyHoldFileInfo(HoldFileInfo input)
    {
      try
      {
        return this.Modify((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse InsertHoldFileInfo(HoldFileInfo input)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Insert((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse DeleteHoldFileInfo(HoldFileInfo input)
    {
      try
      {
        return this.Delete((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public int Retrieve(HazMatCommodity filter, out HazMatCommodity output, out Error error)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Retrieve<HazMatCommodity>(filter, out output, out error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        output = (HazMatCommodity) null;
        error = new Error(4);
        return 4;
      }
    }

    public ServiceResponse ModifyHazMatCommodity(HazMatCommodity input)
    {
      try
      {
        return this.Modify((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse InsertHazMatCommodity(HazMatCommodity input)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Insert((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse DeleteHazMatCommodity(HazMatCommodity input)
    {
      try
      {
        return this.Delete((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public int Retrieve(
      FreightBillOfLadingLine filter,
      out FreightBillOfLadingLine output,
      out Error error)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Retrieve<FreightBillOfLadingLine>(filter, out output, out error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        output = (FreightBillOfLadingLine) null;
        error = new Error(4);
        return 4;
      }
    }

    public ServiceResponse ModifyFreightBillOfLadingLine(FreightBillOfLadingLine input)
    {
      try
      {
        return this.Modify((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse InsertFreightBillOfLadingLine(FreightBillOfLadingLine input)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Insert((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse DeleteFreightBillOfLadingLine(FreightBillOfLadingLine input)
    {
      try
      {
        return this.Delete((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public int Retrieve(DGCommodity filter, out DGCommodity output, out Error error)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Retrieve<DGCommodity>(filter, out output, out error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        output = (DGCommodity) null;
        error = new Error(4);
        return 4;
      }
    }

    public ServiceResponse ModifyDGCommodity(DGCommodity input)
    {
      try
      {
        return this.Modify((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse InsertDGCommodity(DGCommodity input)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Insert((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse DeleteDGCommodity(DGCommodity input)
    {
      try
      {
        return this.Delete((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public int Retrieve(ThirdPartyAccount filter, out ThirdPartyAccount output, out Error error)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Retrieve<ThirdPartyAccount>(filter, out output, out error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        output = (ThirdPartyAccount) null;
        error = new Error(4);
        return 4;
      }
    }

    public ServiceResponse ModifyThirdPartyAccount(ThirdPartyAccount input)
    {
      try
      {
        return this.Modify((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse InsertThirdPartyAccount(ThirdPartyAccount input)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Insert((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse DeleteThirdPartyAccount(ThirdPartyAccount input)
    {
      try
      {
        return this.Delete((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public int Retrieve(InboundReceive filter, out InboundReceive output, out Error error)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Retrieve<InboundReceive>(filter, out output, out error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        output = (InboundReceive) null;
        error = new Error(4);
        return 4;
      }
    }

    public ServiceResponse ModifyInboundReceive(InboundReceive input)
    {
      try
      {
        return this.Modify((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse InsertInboundReceive(InboundReceive input)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Insert((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse DeleteInboundReceive(InboundReceive input)
    {
      try
      {
        return this.Delete((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public int Retrieve(CustomLabelConfig filter, out CustomLabelConfig output, out Error error)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Retrieve<CustomLabelConfig>(filter, out output, out error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        output = (CustomLabelConfig) null;
        error = new Error(4);
        return 4;
      }
    }

    public ServiceResponse ModifyCustomLabelConfig(CustomLabelConfig input)
    {
      try
      {
        return this.Modify((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse InsertCustomLabelConfig(CustomLabelConfig input)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Insert((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse DeleteCustomLabelConfig(CustomLabelConfig input)
    {
      try
      {
        return this.Delete((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public int Retrieve(ImageDocument filter, out ImageDocument output, out Error error)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Retrieve<ImageDocument>(filter, out output, out error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        output = (ImageDocument) null;
        error = new Error(4);
        return 4;
      }
    }

    public ServiceResponse ModifyImageDocument(ImageDocument input)
    {
      try
      {
        return this.Modify((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse InsertImageDocument(ImageDocument input)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Insert((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse DeleteImageDocument(ImageDocument input)
    {
      try
      {
        return this.Delete((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public int Retrieve(WSEDInfo filter, out WSEDInfo output, out Error error)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Retrieve<WSEDInfo>(filter, out output, out error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        output = (WSEDInfo) null;
        error = new Error(4);
        return 4;
      }
    }

    public ServiceResponse ModifyWSEDInfo(WSEDInfo input)
    {
      try
      {
        return this.Modify((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse InsertWSEDInfo(WSEDInfo input)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Insert((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse DeleteWSEDInfo(WSEDInfo input)
    {
      try
      {
        return this.Delete((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ModifyIShipDefl(IShipDefl input)
    {
      try
      {
        return this.Modify((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse InsertIShipDefl(IShipDefl input)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Insert((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse DeleteIShipDefl(IShipDefl input)
    {
      try
      {
        return this.Delete((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ModifyDShipDefl(DShipDefl input)
    {
      try
      {
        return this.Modify((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse InsertDShipDefl(DShipDefl input)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Insert((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse DeleteDShipDefl(DShipDefl input)
    {
      try
      {
        return this.Delete((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ModifyFShipDefl(FShipDefl input)
    {
      try
      {
        return this.Modify((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse InsertFShipDefl(FShipDefl input)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Insert((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse DeleteFShipDefl(FShipDefl input)
    {
      try
      {
        return this.Delete((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ModifyTDShipDefl(TDShipDefl input)
    {
      try
      {
        return this.Modify((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse InsertTDShipDefl(TDShipDefl input)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Insert((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse DeleteTDShipDefl(TDShipDefl input)
    {
      try
      {
        return this.Delete((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public int Retrieve(ValidatorConfig filter, out ValidatorConfig output, out Error error)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Retrieve<ValidatorConfig>(filter, out output, out error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        output = (ValidatorConfig) null;
        error = new Error(4);
        return 4;
      }
    }

    public ServiceResponse ModifyValidatorConfig(ValidatorConfig input)
    {
      try
      {
        return this.Modify((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse InsertValidatorConfig(ValidatorConfig input)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Insert((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse DeleteValidatorConfig(ValidatorConfig input)
    {
      try
      {
        return this.Delete((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse ModifyMessageQueueEntry(MessageQueueEntry input)
    {
      try
      {
        return this.Modify((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse InsertMessageQueueEntry(MessageQueueEntry input)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Insert((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse DeleteMessageQueueEntry(MessageQueueEntry input)
    {
      try
      {
        return this.Delete((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse InsertPackageLabelSpeed(PackageLabelSpeed input)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Insert((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public int Retrieve(MasterShipment filter, out MasterShipment output, out Error error)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Retrieve<MasterShipment>(filter, out output, out error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        output = (MasterShipment) null;
        error = new Error(4);
        return 4;
      }
    }

    public ServiceResponse ModifyMasterShipment(MasterShipment input)
    {
      try
      {
        return this.Modify((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse InsertMasterShipment(MasterShipment input)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Insert((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse DeleteMasterShipment(MasterShipment input)
    {
      try
      {
        return this.Delete((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public int Retrieve(IDDPreferences filter, out IDDPreferences output, out Error error)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Retrieve<IDDPreferences>(filter, out output, out error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        output = (IDDPreferences) null;
        error = new Error(4);
        return 4;
      }
    }

    public ServiceResponse ModifyIDDPreferences(IDDPreferences input)
    {
      try
      {
        return this.Modify((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse InsertIDDPreferences(IDDPreferences input)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Insert((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse DeleteIDDPreferences(IDDPreferences input)
    {
      try
      {
        return this.Delete((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public int Retrieve(
      CommInvConsDataList filter,
      out CommInvConsDataList output,
      out Error error)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Retrieve<CommInvConsDataList>(filter, out output, out error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        output = (CommInvConsDataList) null;
        error = new Error(4);
        return 4;
      }
    }

    public AccountListResponse GetAccountList(Account filter)
    {
      try
      {
        return new AccountListResponse()
        {
          Accounts = ShipEngineComponents.DataAccessService.GetObjectList<Account>(filter)
        };
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        AccountListResponse accountList = new AccountListResponse();
        accountList.Error = new Error(4);
        return accountList;
      }
    }

    public CloseInfoListResponse GetCloseInfoList(CloseInfo filter)
    {
      try
      {
        return new CloseInfoListResponse()
        {
          Closes = ShipEngineComponents.DataAccessService.GetObjectList<CloseInfo>(filter)
        };
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        CloseInfoListResponse closeInfoList = new CloseInfoListResponse();
        closeInfoList.Error = new Error(4);
        return closeInfoList;
      }
    }

    public CloseInfoListResponse GetCloseInfoList(CloseInfo filter, int type)
    {
      try
      {
        return new CloseInfoListResponse()
        {
          Closes = ShipEngineComponents.DataAccessService.GetCloseInfoList(filter, type)
        };
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        CloseInfoListResponse closeInfoList = new CloseInfoListResponse();
        closeInfoList.Error = new Error(4);
        return closeInfoList;
      }
    }

    public FormSettingListResponse GetFormSettingList(FormSetting filter)
    {
      try
      {
        return new FormSettingListResponse()
        {
          FormSettings = ShipEngineComponents.DataAccessService.GetObjectList<FormSetting>(filter)
        };
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        FormSettingListResponse formSettingList = new FormSettingListResponse();
        formSettingList.Error = new Error(4);
        return formSettingList;
      }
    }

    public DeltaHistoryListResponse GetDeltaHistoryList(DeltaHistory filter)
    {
      try
      {
        return new DeltaHistoryListResponse()
        {
          DeltaHistories = ShipEngineComponents.DataAccessService.GetObjectList<DeltaHistory>(filter)
        };
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        DeltaHistoryListResponse deltaHistoryList = new DeltaHistoryListResponse();
        deltaHistoryList.Error = new Error(4);
        return deltaHistoryList;
      }
    }

    public TemplateListResponse GetTemplateList(SFCTemplate filter)
    {
      try
      {
        return new TemplateListResponse()
        {
          Templates = ShipEngineComponents.DataAccessService.GetObjectList<SFCTemplate>(filter)
        };
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        TemplateListResponse templateList = new TemplateListResponse();
        templateList.Error = new Error(4);
        return templateList;
      }
    }

    public RecipientListResponse GetRecipientList(Recipient filter)
    {
      try
      {
        return new RecipientListResponse()
        {
          Recipients = ShipEngineComponents.DataAccessService.GetObjectList<Recipient>(filter)
        };
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        RecipientListResponse recipientList = new RecipientListResponse();
        recipientList.Error = new Error(4);
        return recipientList;
      }
    }

    public SenderListResponse GetSenderList(Sender filter)
    {
      try
      {
        return new SenderListResponse()
        {
          Senders = ShipEngineComponents.DataAccessService.GetObjectList<Sender>(filter)
        };
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        SenderListResponse senderList = new SenderListResponse();
        senderList.Error = new Error(4);
        return senderList;
      }
    }

    public BrokerListResponse GetBrokerList(Broker filter)
    {
      try
      {
        return new BrokerListResponse()
        {
          Brokers = ShipEngineComponents.DataAccessService.GetObjectList<Broker>(filter)
        };
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        BrokerListResponse brokerList = new BrokerListResponse();
        brokerList.Error = new Error(4);
        return brokerList;
      }
    }

    public CommodityListResponse GetCommodityList(Commodity filter)
    {
      try
      {
        return new CommodityListResponse()
        {
          Commodities = ShipEngineComponents.DataAccessService.GetObjectList<Commodity>(filter)
        };
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        CommodityListResponse commodityList = new CommodityListResponse();
        commodityList.Error = new Error(4);
        return commodityList;
      }
    }

    public DGCommodityListResponse GetDGCommodityList(DGCommodity filter)
    {
      try
      {
        return new DGCommodityListResponse()
        {
          DGCommodities = ShipEngineComponents.DataAccessService.GetObjectList<DGCommodity>(filter)
        };
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        DGCommodityListResponse dgCommodityList = new DGCommodityListResponse();
        dgCommodityList.Error = new Error(4);
        return dgCommodityList;
      }
    }

    public DepartmentListResponse GetDepartmentList(Department filter)
    {
      try
      {
        return new DepartmentListResponse()
        {
          Departments = ShipEngineComponents.DataAccessService.GetObjectList<Department>(filter)
        };
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        DepartmentListResponse departmentList = new DepartmentListResponse();
        departmentList.Error = new Error(4);
        return departmentList;
      }
    }

    public DimensionListResponse GetDimensionList(Dimension filter)
    {
      try
      {
        return new DimensionListResponse()
        {
          Dimensions = ShipEngineComponents.DataAccessService.GetObjectList<Dimension>(filter)
        };
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        DimensionListResponse dimensionList = new DimensionListResponse();
        dimensionList.Error = new Error(4);
        return dimensionList;
      }
    }

    public HazMatCommodityListResponse GetHazMatCommodityList(HazMatCommodity filter)
    {
      try
      {
        return new HazMatCommodityListResponse()
        {
          HazMatCommodities = ShipEngineComponents.DataAccessService.GetObjectList<HazMatCommodity>(filter)
        };
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        HazMatCommodityListResponse matCommodityList = new HazMatCommodityListResponse();
        matCommodityList.Error = new Error(4);
        return matCommodityList;
      }
    }

    public IPDImporterOfRecordListResponse GetIPDImporterOfRecordList(IPDImporterOfRecord filter)
    {
      try
      {
        return new IPDImporterOfRecordListResponse()
        {
          IPDImporterOfRecords = ShipEngineComponents.DataAccessService.GetObjectList<IPDImporterOfRecord>(filter)
        };
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        IPDImporterOfRecordListResponse importerOfRecordList = new IPDImporterOfRecordListResponse();
        importerOfRecordList.Error = new Error(4);
        return importerOfRecordList;
      }
    }

    public RecipientGroupListResponse GetRecipientGroupList(RecipientGroup filter)
    {
      try
      {
        return new RecipientGroupListResponse()
        {
          RecipientGroups = ShipEngineComponents.DataAccessService.GetObjectList<RecipientGroup>(filter)
        };
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        RecipientGroupListResponse recipientGroupList = new RecipientGroupListResponse();
        recipientGroupList.Error = new Error(4);
        return recipientGroupList;
      }
    }

    public ReferenceListResponse GetReferenceList(Reference filter)
    {
      try
      {
        return new ReferenceListResponse()
        {
          References = ShipEngineComponents.DataAccessService.GetObjectList<Reference>(filter)
        };
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        ReferenceListResponse referenceList = new ReferenceListResponse();
        referenceList.Error = new Error(4);
        return referenceList;
      }
    }

    public SFCTemplateListResponse GetSFCTemplateList(SFCTemplate filter)
    {
      try
      {
        return new SFCTemplateListResponse()
        {
          SFCTemplates = ShipEngineComponents.DataAccessService.GetObjectList<SFCTemplate>(filter)
        };
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        SFCTemplateListResponse sfcTemplateList = new SFCTemplateListResponse();
        sfcTemplateList.Error = new Error(4);
        return sfcTemplateList;
      }
    }

    public UserListResponse GetUserList(User filter)
    {
      try
      {
        return new UserListResponse()
        {
          Users = ShipEngineComponents.DataAccessService.GetObjectList<User>(filter)
        };
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        UserListResponse userList = new UserListResponse();
        userList.Error = new Error(4);
        return userList;
      }
    }

    public ShipProfileListResponse GetShipProfileList(ShipProfile filter)
    {
      try
      {
        return new ShipProfileListResponse()
        {
          ShipProfiles = ShipEngineComponents.DataAccessService.GetObjectList<ShipProfile>(filter)
        };
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        ShipProfileListResponse shipProfileList = new ShipProfileListResponse();
        shipProfileList.Error = new Error(4);
        return shipProfileList;
      }
    }

    public DShipDeflListResponse GetDShipDeflList(DShipDefl filter)
    {
      try
      {
        return new DShipDeflListResponse()
        {
          DShipDefls = ShipEngineComponents.DataAccessService.GetObjectList<DShipDefl>(filter)
        };
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        DShipDeflListResponse dshipDeflList = new DShipDeflListResponse();
        dshipDeflList.Error = new Error(4);
        return dshipDeflList;
      }
    }

    public IShipDeflListResponse GetIShipDeflList(IShipDefl filter)
    {
      try
      {
        return new IShipDeflListResponse()
        {
          IShipDefls = ShipEngineComponents.DataAccessService.GetObjectList<IShipDefl>(filter)
        };
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        IShipDeflListResponse ishipDeflList = new IShipDeflListResponse();
        ishipDeflList.Error = new Error(4);
        return ishipDeflList;
      }
    }

    public TDShipDeflListResponse GetTDShipDeflList(TDShipDefl filter)
    {
      try
      {
        return new TDShipDeflListResponse()
        {
          TDShipDefls = ShipEngineComponents.DataAccessService.GetObjectList<TDShipDefl>(filter)
        };
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        TDShipDeflListResponse tdShipDeflList = new TDShipDeflListResponse();
        tdShipDeflList.Error = new Error(4);
        return tdShipDeflList;
      }
    }

    public FShipDeflListResponse GetFShipDeflList(FShipDefl filter)
    {
      try
      {
        return new FShipDeflListResponse()
        {
          FShipDefls = ShipEngineComponents.DataAccessService.GetObjectList<FShipDefl>(filter)
        };
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        FShipDeflListResponse fshipDeflList = new FShipDeflListResponse();
        fshipDeflList.Error = new Error(4);
        return fshipDeflList;
      }
    }

    public SystemPrefsListResponse GetSystemPrefsList(SystemPrefs filter)
    {
      try
      {
        return new SystemPrefsListResponse()
        {
          SystemPrefs = ShipEngineComponents.DataAccessService.GetObjectList<SystemPrefs>(filter)
        };
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        SystemPrefsListResponse systemPrefsList = new SystemPrefsListResponse();
        systemPrefsList.Error = new Error(4);
        return systemPrefsList;
      }
    }

    public FreightAccountListResponse GetFreightAccountList(FreightAccount filter)
    {
      try
      {
        return new FreightAccountListResponse()
        {
          FreightAccounts = ShipEngineComponents.DataAccessService.GetObjectList<FreightAccount>(filter)
        };
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        FreightAccountListResponse freightAccountList = new FreightAccountListResponse();
        freightAccountList.Error = new Error(4);
        return freightAccountList;
      }
    }

    public FreightBillOfLadingLineListResponse GetFreightBillOfLadingLineList(
      FreightBillOfLadingLine filter)
    {
      try
      {
        return new FreightBillOfLadingLineListResponse()
        {
          FreightBillOfLadingLines = ShipEngineComponents.DataAccessService.GetObjectList<FreightBillOfLadingLine>(filter)
        };
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        FreightBillOfLadingLineListResponse ofLadingLineList = new FreightBillOfLadingLineListResponse();
        ofLadingLineList.Error = new Error(4);
        return ofLadingLineList;
      }
    }

    public CustomLabelConfigListResponse GetCustomLabelConfigList(CustomLabelConfig filter)
    {
      try
      {
        return new CustomLabelConfigListResponse()
        {
          CustomLabelConfigs = ShipEngineComponents.DataAccessService.GetObjectList<CustomLabelConfig>(filter)
        };
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        CustomLabelConfigListResponse customLabelConfigList = new CustomLabelConfigListResponse();
        customLabelConfigList.Error = new Error(4);
        return customLabelConfigList;
      }
    }

    public ValidatorConfigListResponse GetValidatorConfigList(ValidatorConfig filter)
    {
      try
      {
        return new ValidatorConfigListResponse()
        {
          ValidatorConfigs = ShipEngineComponents.DataAccessService.GetObjectList<ValidatorConfig>(filter)
        };
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        ValidatorConfigListResponse validatorConfigList = new ValidatorConfigListResponse();
        validatorConfigList.Error = new Error(4);
        return validatorConfigList;
      }
    }

    public ImageDocumentListResponse GetImageDocumentList(ImageDocument filter)
    {
      try
      {
        return new ImageDocumentListResponse()
        {
          ImageDocuments = ShipEngineComponents.DataAccessService.GetObjectList<ImageDocument>(filter)
        };
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        ImageDocumentListResponse imageDocumentList = new ImageDocumentListResponse();
        imageDocumentList.Error = new Error(4);
        return imageDocumentList;
      }
    }

    public ThirdPartyAccountListResponse GetThirdPartyAccountList(ThirdPartyAccount filter)
    {
      try
      {
        return new ThirdPartyAccountListResponse()
        {
          ThirdPartyAccounts = ShipEngineComponents.DataAccessService.GetObjectList<ThirdPartyAccount>(filter)
        };
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        ThirdPartyAccountListResponse partyAccountList = new ThirdPartyAccountListResponse();
        partyAccountList.Error = new Error(4);
        return partyAccountList;
      }
    }

    public LTLFreightTemplateListResponse GetLTLFreightTemplateList(LTLFreightTemplate filter)
    {
      try
      {
        return new LTLFreightTemplateListResponse()
        {
          LTLFreightTemplates = ShipEngineComponents.DataAccessService.GetObjectList<LTLFreightTemplate>(filter)
        };
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        LTLFreightTemplateListResponse freightTemplateList = new LTLFreightTemplateListResponse();
        freightTemplateList.Error = new Error(4);
        return freightTemplateList;
      }
    }

    public IDDPreferencesListResponse GetIDDPreferencesList(IDDPreferences filter)
    {
      try
      {
        return new IDDPreferencesListResponse()
        {
          IDDPreferences = ShipEngineComponents.DataAccessService.GetObjectList<IDDPreferences>(filter)
        };
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        IDDPreferencesListResponse iddPreferencesList = new IDDPreferencesListResponse();
        iddPreferencesList.Error = new Error(4);
        return iddPreferencesList;
      }
    }

    public ServiceResponse SaveIDD3rdPartyPackage(IDD3rdPartyShip IddPackage, bool isAdd)
    {
      try
      {
        return ShipEngineComponents.ShipEngine.SaveIDD3rdPartyPackage(IddPackage, isAdd);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse InsertWithoutValidation(Recipient input)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.InsertWithoutValidation((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse InsertWithoutValidation(Sender input)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.InsertWithoutValidation((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse InsertWithoutValidation(Broker input)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.InsertWithoutValidation((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse InsertWithoutValidation(Commodity input)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.InsertWithoutValidation((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse InsertWithoutValidation(DGCommodity input)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.InsertWithoutValidation((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse InsertWithoutValidation(Department input)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.InsertWithoutValidation((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse InsertWithoutValidation(Dimension input)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.InsertWithoutValidation((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse InsertWithoutValidation(HazMatCommodity input)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.InsertWithoutValidation((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse InsertWithoutValidation(IPDImporterOfRecord input)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.InsertWithoutValidation((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse InsertWithoutValidation(RecipientGroup input)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.InsertWithoutValidation((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse InsertWithoutValidation(Reference input)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.InsertWithoutValidation((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse InsertWithoutValidation(SFCTemplate input)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.InsertWithoutValidation((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse InsertWithoutValidation(User input)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.InsertWithoutValidation((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse InsertWithoutValidation(ShipProfile input)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.InsertWithoutValidation((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse InsertWithoutValidation(DShipDefl input)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.InsertWithoutValidation((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse InsertWithoutValidation(IShipDefl input)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.InsertWithoutValidation((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse InsertWithoutValidation(TDShipDefl input)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.InsertWithoutValidation((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse InsertWithoutValidation(FShipDefl input)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.InsertWithoutValidation((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse InsertWithoutValidation(SystemPrefs input)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.InsertWithoutValidation((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse InsertWithoutValidation(FreightAccount input)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.InsertWithoutValidation((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse InsertWithoutValidation(FreightBillOfLadingLine input)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.InsertWithoutValidation((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse InsertWithoutValidation(CustomLabelConfig input)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.InsertWithoutValidation((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse InsertWithoutValidation(ValidatorConfig input)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.InsertWithoutValidation((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse InsertWithoutValidation(ImageDocument input)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.InsertWithoutValidation((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse InsertWithoutValidation(ThirdPartyAccount input)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.InsertWithoutValidation((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse InsertWithoutValidation(LTLFreightTemplate input)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.InsertWithoutValidation((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse DeleteAllRecipient()
    {
      try
      {
        Error error = new Error();
        ShipEngineComponents.DataAccessService.DeleteAllRows((object) new Recipient(), error);
        return new ServiceResponse(error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse DeleteAllSender()
    {
      try
      {
        Error error = new Error();
        ShipEngineComponents.DataAccessService.DeleteAllRows((object) new Sender(), error);
        return new ServiceResponse(error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse DeleteAllBroker()
    {
      try
      {
        Error error = new Error();
        ShipEngineComponents.DataAccessService.DeleteAllRows((object) new Broker(), error);
        return new ServiceResponse(error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse DeleteAllCommodity()
    {
      try
      {
        Error error = new Error();
        ShipEngineComponents.DataAccessService.DeleteAllRows((object) new Commodity(), error);
        return new ServiceResponse(error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse DeleteAllDGCommodity()
    {
      try
      {
        Error error = new Error();
        ShipEngineComponents.DataAccessService.DeleteAllRows((object) new DGCommodity(), error);
        return new ServiceResponse(error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse DeleteAllDepartment()
    {
      try
      {
        Error error = new Error();
        ShipEngineComponents.DataAccessService.DeleteAllRows((object) new Department(), error);
        return new ServiceResponse(error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse DeleteAllDimension()
    {
      try
      {
        Error error = new Error();
        ShipEngineComponents.DataAccessService.DeleteAllRows((object) new Dimension(), error);
        return new ServiceResponse(error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse DeleteAllHazMatCommodity()
    {
      try
      {
        Error error = new Error();
        ShipEngineComponents.DataAccessService.DeleteAllRows((object) new HazMatCommodity(), error);
        return new ServiceResponse(error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse DeleteAllIPDImporterOfRecord()
    {
      try
      {
        Error error = new Error();
        ShipEngineComponents.DataAccessService.DeleteAllRows((object) new IPDImporterOfRecord(), error);
        return new ServiceResponse(error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse DeleteAllRecipientGroup()
    {
      try
      {
        Error error = new Error();
        ShipEngineComponents.DataAccessService.DeleteAllRows((object) new RecipientGroup(), error);
        return new ServiceResponse(error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse DeleteAllReference()
    {
      try
      {
        Error error = new Error();
        ShipEngineComponents.DataAccessService.DeleteAllRows((object) new Reference(), error);
        return new ServiceResponse(error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse DeleteAllSystemSetting()
    {
      try
      {
        Error error = new Error();
        ShipEngineComponents.DataAccessService.DeleteAllRows((object) new SystemSetting(), error);
        return new ServiceResponse(error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse DeleteAllSFCTemplate()
    {
      try
      {
        Error error = new Error();
        ShipEngineComponents.DataAccessService.DeleteAllRows((object) new SFCTemplate(), error);
        return new ServiceResponse(error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse DeleteAllUser()
    {
      try
      {
        Error error = new Error();
        ShipEngineComponents.DataAccessService.DeleteAllRows((object) new User(), error);
        return new ServiceResponse(error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse DeleteAllShipProfile()
    {
      try
      {
        Error error = new Error();
        ShipEngineComponents.DataAccessService.DeleteAllRows((object) new ShipProfile(), error);
        return new ServiceResponse(error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse DeleteAllDShipDefl()
    {
      try
      {
        Error error = new Error();
        ShipEngineComponents.DataAccessService.DeleteAllRows((object) new DShipDefl(), error);
        return new ServiceResponse(error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse DeleteAllIShipDefl()
    {
      try
      {
        Error error = new Error();
        ShipEngineComponents.DataAccessService.DeleteAllRows((object) new IShipDefl(), error);
        return new ServiceResponse(error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse DeleteAllTDShipDefl()
    {
      try
      {
        Error error = new Error();
        ShipEngineComponents.DataAccessService.DeleteAllRows((object) new TDShipDefl(), error);
        return new ServiceResponse(error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse DeleteAllFShipDefl()
    {
      try
      {
        Error error = new Error();
        ShipEngineComponents.DataAccessService.DeleteAllRows((object) new FShipDefl(), error);
        return new ServiceResponse(error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse DeleteAllSystemPrefs()
    {
      try
      {
        Error error = new Error();
        ShipEngineComponents.DataAccessService.DeleteAllRows((object) new SystemPrefs(), error);
        return new ServiceResponse(error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse DeleteAllFreightAccount()
    {
      try
      {
        Error error = new Error();
        ShipEngineComponents.DataAccessService.DeleteAllRows((object) new FreightAccount(), error);
        return new ServiceResponse(error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse DeleteAllFreightBillOfLadingLine()
    {
      try
      {
        Error error = new Error();
        ShipEngineComponents.DataAccessService.DeleteAllRows((object) new FreightBillOfLadingLine(), error);
        return new ServiceResponse(error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse DeleteAllCustomLabelConfig()
    {
      try
      {
        Error error = new Error();
        ShipEngineComponents.DataAccessService.DeleteAllRows((object) new CustomLabelConfig(), error);
        return new ServiceResponse(error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse DeleteAllValidatorConfig()
    {
      try
      {
        Error error = new Error();
        ShipEngineComponents.DataAccessService.DeleteAllRows((object) new ValidatorConfig(), error);
        return new ServiceResponse(error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse DeleteAllImageDocument()
    {
      try
      {
        Error error = new Error();
        ShipEngineComponents.DataAccessService.DeleteAllRows((object) new ImageDocument(), error);
        return new ServiceResponse(error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse DeleteAllThirdPartyAccount()
    {
      try
      {
        Error error = new Error();
        ShipEngineComponents.DataAccessService.DeleteAllRows((object) new ThirdPartyAccount(), error);
        return new ServiceResponse(error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse DeleteAllLTLFreightTemplate()
    {
      try
      {
        Error error = new Error();
        ShipEngineComponents.DataAccessService.DeleteAllRows((object) new LTLFreightTemplate(), error);
        return new ServiceResponse(error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse DeleteAllIDDPreferences()
    {
      try
      {
        Error error = new Error();
        ShipEngineComponents.DataAccessService.DeleteAllRows((object) new IDDPreferences(), error);
        return new ServiceResponse(error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse InsertWithoutValidation(IDDPreferences input)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.InsertWithoutValidation((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public bool IsReachable() => true;

    public int GetDataListFilteredSetWithDetails(
      FedEx.Gsm.ShipEngine.Entities.ListSpecification listType,
      out DataSet output,
      List<GsmFilter> filterList,
      List<GsmSort> sortList,
      List<string> columnNames,
      Error error)
    {
      return this.GetDataList((object) null, listType, out output, filterList, sortList, columnNames, error);
    }

    public int GetDataListFilteredTable(
      FedEx.Gsm.ShipEngine.Entities.ListSpecification listType,
      out DataTable output,
      Error error)
    {
      return this.GetDataList((object) null, listType, out output, error);
    }

    public int GetDataListFilteredTableWithDetails(
      FedEx.Gsm.ShipEngine.Entities.ListSpecification listType,
      out DataTable output,
      List<GsmFilter> filterList,
      List<GsmSort> sortList,
      List<string> columnNames,
      Error error)
    {
      return this.GetDataList((object) null, listType, out output, filterList, sortList, columnNames, error);
    }

    public DataSetResponse GetDataSetListFilteredWithDetails(
      FedEx.Gsm.ShipEngine.Entities.ListSpecification listType,
      List<GsmFilter> filterList,
      List<GsmSort> sortList,
      List<string> columnNames)
    {
      return this.GetDataSetList((object) null, listType, filterList, sortList, columnNames);
    }

    public DataTableResponse GetDataTableListFilteredWithDetails(
      FedEx.Gsm.ShipEngine.Entities.ListSpecification listType,
      List<GsmFilter> filterList,
      List<GsmSort> sortList,
      List<string> columnNames)
    {
      return this.GetDataTableList((object) null, listType, filterList, sortList, columnNames);
    }

    public ServiceResponse InsertDefaultDShipDefl(DefaultDShipDefl input)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Insert((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse InsertDefaultIShipDefl(DefaultIShipDefl input)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Insert((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse InsertDefaultFShipDefl(DefaultFShipDefl input)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Insert((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse InsertDefaultTDShipDefl(DefaultTDShipDefl input)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Insert((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public int Retrieve(
      IPDImporterOfRecord filter,
      out IPDImporterOfRecord output,
      out Error error)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Retrieve<IPDImporterOfRecord>(filter, out output, out error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        output = (IPDImporterOfRecord) null;
        error = new Error(4);
        return 4;
      }
    }

    public ServiceResponse ModifyIPDImporterOfRecord(IPDImporterOfRecord input)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Modify((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse InsertIPDImporterOfRecord(IPDImporterOfRecord input)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Insert((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse DeleteIPDImporterOfRecord(IPDImporterOfRecord input)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Delete((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public int Retrieve(EULAInfor filter, out EULAInfor output, out Error error)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Retrieve<EULAInfor>(filter, out output, out error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        output = (EULAInfor) null;
        error = new Error(4);
        return 4;
      }
    }

    public ServiceResponse ModifyEula(EULAInfor input)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Modify((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse InsertEula(EULAInfor input)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Insert((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse DeleteEula(EULAInfor input)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Delete((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public int Retrieve(SystemInfo filter, out SystemInfo output, out Error error)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Retrieve<SystemInfo>(filter, out output, out error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        output = (SystemInfo) null;
        error = new Error(4);
        return 4;
      }
    }

    public int Retrieve(TrackingNumber filter, out TrackingNumber output, out Error error)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Retrieve<TrackingNumber>(filter, out output, out error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        output = (TrackingNumber) null;
        error = new Error(4);
        return 4;
      }
    }

    public EulaInfoListResponse GetEulaList(EULAInfor filter)
    {
      try
      {
        return new EulaInfoListResponse()
        {
          Eulas = ShipEngineComponents.DataAccessService.GetObjectList<EULAInfor>(filter)
        };
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        EulaInfoListResponse eulaList = new EulaInfoListResponse();
        eulaList.Error = new Error(4);
        return eulaList;
      }
    }

    public int Retrieve(FreightShipment filter, out FreightShipment output, out Error error)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Retrieve<FreightShipment>(filter, out output, out error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        output = (FreightShipment) null;
        error = new Error(4);
        return 4;
      }
    }

    public bool AddToRateCount(TrackRateCount input, out Error error)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.AddToRateCount(input, out error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        error = new Error(4);
        return false;
      }
    }

    public int Retrieve(IDD3rdPartyShip filter, out IDD3rdPartyShip output, out Error error)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Retrieve<IDD3rdPartyShip>(filter, out output, out error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        output = (IDD3rdPartyShip) null;
        error = new Error(4);
        return 4;
      }
    }

    public ServiceResponse DeleteIDD3rdPartyShip(IDD3rdPartyShip input)
    {
      try
      {
        return DataAccessServiceImp.GetInstance().Delete((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public int Retrieve(UploadFile filter, out UploadFile output, out Error error)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Retrieve<UploadFile>(filter, out output, out error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        output = (UploadFile) null;
        error = new Error(4);
        return 4;
      }
    }

    public ServiceResponse ModifyUploadFile(UploadFile input)
    {
      try
      {
        return DataAccessServiceImp.GetInstance().Modify((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse InsertUploadFile(UploadFile input)
    {
      try
      {
        return DataAccessServiceImp.GetInstance().Insert((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public ServiceResponse DeleteUploadFile(UploadFile input)
    {
      try
      {
        return DataAccessServiceImp.GetInstance().Delete((object) input);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new ServiceResponse(new Error(4));
      }
    }

    public int Retrieve(FreightPickup filter, out FreightPickup output, out Error error)
    {
      try
      {
        return ShipEngineComponents.DataAccessService.Retrieve<FreightPickup>(filter, out output, out error);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        output = (FreightPickup) null;
        error = new Error(4);
        return 4;
      }
    }

    public WaldoInfo WheresWaldoNow(Shipment shipment, SplSvc svc)
    {
      try
      {
        return ShipEngineComponents.EditService.WheresWaldoNow(shipment, svc);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return new WaldoInfo();
      }
    }

    public bool IsApiRegistrationNeeded()
    {
      FedEx.Gsm.Common.ConfigManager.ConfigManager configManager = new FedEx.Gsm.Common.ConfigManager.ConfigManager();
      string str = !configManager.IsEngineOnly ? "CAFE" : "GSMW";
      OAuthCredentials output;
      return ShipEngineComponents.DataAccessService.Retrieve<OAuthCredentials>(new OAuthCredentials()
      {
        Product = str,
        SystemLevel = configManager.BackendEnvironment
      }, out output, out Error _) != 1 || output == null || string.IsNullOrEmpty(output.Child_Key) || string.IsNullOrEmpty(output.Child_Secret) || string.IsNullOrEmpty(output.Pairing_Token);
    }

    public bool IsMrnRequiredForShipment(Shipment shipment)
    {
      try
      {
        return ShipEngineComponents.EditService.IsMrnRequired(shipment);
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        return false;
      }
    }

    public WaldoBatchResponse WheresWaldoNowBatch(Shipment ship, List<SplSvc> sss)
    {
      try
      {
        return new WaldoBatchResponse()
        {
          WaldoInfos = sss.Select<SplSvc, KeyValuePair<SplSvc, WaldoInfo>>((System.Func<SplSvc, KeyValuePair<SplSvc, WaldoInfo>>) (ss => new KeyValuePair<SplSvc, WaldoInfo>(ss, ShipEngineComponents.EditService.WheresWaldoNow(ship, ss)))).ToList<KeyValuePair<SplSvc, WaldoInfo>>()
        };
      }
      catch (Exception ex)
      {
        ShipEngineCommonUtilities.DumpExceptionToFile(ex);
        WaldoBatchResponse waldoBatchResponse = new WaldoBatchResponse();
        waldoBatchResponse.Error = new Error(4);
        return waldoBatchResponse;
      }
    }
  }
}
