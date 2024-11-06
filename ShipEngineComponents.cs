// Decompiled with JetBrains decompiler
// Type: FedEx.Gsm.ShipEngine.ShipEngineFacade.ShipEngineComponents
// Assembly: FedEx.Gsm.ShipEngine.ShipEngineFacade, Version=38.55.1083.0, Culture=neutral, PublicKeyToken=null
// MVID: 9EFD23F2-2AC9-4A0F-A69E-989CFB0B71BB
// Assembly location: C:\Users\elsmi\OneDrive\Desktop\work\fsm_modified\FedEx_modified\ShipManager\BIN\FedEx.Gsm.ShipEngine.ShipEngineFacade.dll

using FedEx.Gsm.Common.Logging;
using FedEx.Gsm.ShipEngine.CommApi;
using FedEx.Gsm.ShipEngine.DataAccessService;
using FedEx.Gsm.ShipEngine.DGHazmat;
using FedEx.Gsm.ShipEngine.EditService.BusinessLogic;
using FedEx.Gsm.ShipEngine.FreightIntegration.BusinessLogic;
using FedEx.Gsm.ShipEngine.HoldAtLocation;
using FedEx.Gsm.ShipEngine.Pickup.BusinessLogic;
using FedEx.Gsm.ShipEngine.RateService.BusinessLogic;
using FedEx.Gsm.ShipEngine.RevEngine;
using FedEx.Gsm.ShipEngine.Route.BusinessLogic;
using FedEx.Gsm.ShipEngine.ShipService.BusinessLogic;
using FedEx.Gsm.ShipEngine.SmartPost.BusinessLogic;
using FedEx.Gsm.ShipEngine.Tracking.BusinessLogic;
using System;
using System.Diagnostics;
using System.Threading;

#nullable disable
namespace FedEx.Gsm.ShipEngine.ShipEngineFacade
{
  internal static class ShipEngineComponents
  {
    private static object _getInstanceLock = new object();
    private static SmartPostImp _smartPostImp;
    private static TrackServiceImp _trackService;
    private static PickupLogicImp _pickupService;
    private static ShipServiceImp _shipService;
    private static RateServiceImp _rateService;
    private static RevEngineImp _RevService;
    private static EditServiceImp _editService;
    private static APIHandlerLogic _apiService;
    private static FreightIntegrationImp _freightIntegrationService;
    private static CommStatus _commStatus;
    private static FastHazmatServiceImp _hazmatService;
    private static HoldAtLocationImp _halLookupService;
    private static DataAccessServiceImp _dataAccessService;
    private static RouteServiceImp _routeService;

    static ShipEngineComponents()
    {
      new Thread((ThreadStart) (() =>
      {
        Thread.Sleep(300);
        RevEngineImp.GetInstance();
      })).Start();
    }

    public static CommStatus CommunicationStatus
    {
      get
      {
        if (ShipEngineComponents._commStatus == null)
        {
          lock (ShipEngineComponents._getInstanceLock)
          {
            FxLogger.LogMessage(FedEx.Gsm.Common.Logging.LogLevel.Info, FxLogger.AppCode_Default, nameof (CommunicationStatus), "Creating Communication Status Object, Entering");
            ShipEngineComponents._commStatus = CommStatus.GetInstance();
            FxLogger.LogMessage(FedEx.Gsm.Common.Logging.LogLevel.Info, FxLogger.AppCode_Default, nameof (CommunicationStatus), "Creating Communication Status Object, Leaving");
          }
        }
        return ShipEngineComponents._commStatus;
      }
    }

    public static FreightIntegrationImp FreightIntegrationService
    {
      get
      {
        if (ShipEngineComponents._freightIntegrationService == null)
        {
          lock (ShipEngineComponents._getInstanceLock)
          {
            FxLogger.LogMessage(FedEx.Gsm.Common.Logging.LogLevel.Info, FxLogger.AppCode_Default, "FreightIntegration", "Creating FreightIntegration Object");
            ShipEngineComponents._freightIntegrationService = FreightIntegrationImp.GetInstance();
          }
        }
        return ShipEngineComponents._freightIntegrationService;
      }
    }

    public static EditServiceImp EditService
    {
      get
      {
        if (ShipEngineComponents._editService == null)
        {
          lock (ShipEngineComponents._getInstanceLock)
          {
            FxLogger.LogMessage(FedEx.Gsm.Common.Logging.LogLevel.Info, FxLogger.AppCode_Default, nameof (EditService), "Creating EditService Object");
            ShipEngineComponents._editService = EditServiceImp.GetInstance();
          }
        }
        return ShipEngineComponents._editService;
      }
    }

    public static APIHandlerLogic APIService
    {
      get
      {
        if (ShipEngineComponents._apiService == null)
        {
          lock (ShipEngineComponents._getInstanceLock)
          {
            FxLogger.LogMessage(FedEx.Gsm.Common.Logging.LogLevel.Info, FxLogger.AppCode_Default, "APIHandlerLogic", "Creating APIHandlerLogic Object");
            ShipEngineComponents._apiService = APIHandlerLogic.GetInstance();
          }
        }
        return ShipEngineComponents._apiService;
      }
    }

    public static RevEngineImp RevService
    {
      get
      {
        if (ShipEngineComponents._RevService == null)
        {
          lock (ShipEngineComponents._getInstanceLock)
          {
            FxLogger.LogMessage(FedEx.Gsm.Common.Logging.LogLevel.Info, FxLogger.AppCode_Default, nameof (RevService), "Creating RevService Object");
            ShipEngineComponents._RevService = RevEngineImp.GetInstance();
          }
        }
        return ShipEngineComponents._RevService;
      }
    }

    public static RateServiceImp RateService
    {
      get
      {
        if (ShipEngineComponents._rateService == null)
        {
          lock (ShipEngineComponents._getInstanceLock)
          {
            FxLogger.LogMessage(FedEx.Gsm.Common.Logging.LogLevel.Info, FxLogger.AppCode_Default, "Rate Service", "Creating RateService Object");
            ShipEngineComponents._rateService = RateServiceImp.GetInstance();
            try
            {
              Process.GetCurrentProcess().MaxWorkingSet = (IntPtr) ((int) Process.GetCurrentProcess().MaxWorkingSet - 1);
            }
            catch (Exception ex)
            {
              FxLogger.LogMessage(FedEx.Gsm.Common.Logging.LogLevel.Info, FxLogger.AppCode_Default, "WorkingSetStatus", "Set MaxWorkingSet throw an exception");
            }
          }
        }
        return ShipEngineComponents._rateService;
      }
    }

    public static void DisposeRateService()
    {
      if (ShipEngineComponents._rateService == null)
        return;
      ShipEngineComponents._rateService.DisposeRateService();
      ShipEngineComponents._rateService = (RateServiceImp) null;
      GC.Collect();
    }

    public static ShipServiceImp ShipEngine
    {
      get
      {
        if (ShipEngineComponents._shipService == null)
        {
          lock (ShipEngineComponents._getInstanceLock)
          {
            FxLogger.LogMessage(FedEx.Gsm.Common.Logging.LogLevel.Info, FxLogger.AppCode_Default, "ShipService", "Creating ShipService Object");
            ShipEngineComponents._shipService = ShipServiceImp.GetInstance();
          }
        }
        return ShipEngineComponents._shipService;
      }
    }

    public static PickupLogicImp PickupServiceComponent
    {
      get
      {
        if (ShipEngineComponents._pickupService == null)
        {
          lock (ShipEngineComponents._getInstanceLock)
          {
            FxLogger.LogMessage(FedEx.Gsm.Common.Logging.LogLevel.Info, FxLogger.AppCode_Default, "PickupService", "Creating PickupService Object");
            ShipEngineComponents._pickupService = PickupLogicImp.GetInstance();
          }
        }
        return ShipEngineComponents._pickupService;
      }
    }

    public static TrackServiceImp TrackServiceComponent
    {
      get
      {
        if (ShipEngineComponents._trackService == null)
        {
          lock (ShipEngineComponents._getInstanceLock)
          {
            FxLogger.LogMessage(FedEx.Gsm.Common.Logging.LogLevel.Info, FxLogger.AppCode_Default, "TrackService", "Creating TrackService Object");
            ShipEngineComponents._trackService = TrackServiceImp.GetInstance();
          }
        }
        return ShipEngineComponents._trackService;
      }
    }

    public static SmartPostImp SmartPostComponent
    {
      get
      {
        if (ShipEngineComponents._smartPostImp == null)
        {
          lock (ShipEngineComponents._getInstanceLock)
          {
            FxLogger.LogMessage(FedEx.Gsm.Common.Logging.LogLevel.Info, FxLogger.AppCode_Default, "SmartPostService", "Creating SmartPostService Object");
            ShipEngineComponents._smartPostImp = SmartPostImp.GetInstance();
          }
        }
        return ShipEngineComponents._smartPostImp;
      }
    }

    public static FastHazmatServiceImp HazmatService
    {
      get
      {
        if (ShipEngineComponents._hazmatService == null)
        {
          lock (ShipEngineComponents._getInstanceLock)
          {
            FxLogger.LogMessage(FedEx.Gsm.Common.Logging.LogLevel.Info, FxLogger.AppCode_Default, nameof (HazmatService), "Creating HazmatService Object");
            ShipEngineComponents._hazmatService = FastHazmatServiceImp.GetInstance();
          }
        }
        return ShipEngineComponents._hazmatService;
      }
    }

    public static HoldAtLocationImp HALLookupService
    {
      get
      {
        if (ShipEngineComponents._halLookupService == null)
        {
          lock (ShipEngineComponents._getInstanceLock)
          {
            FxLogger.LogMessage(FedEx.Gsm.Common.Logging.LogLevel.Info, FxLogger.AppCode_Default, nameof (HALLookupService), "Creating HALLookupService Object");
            ShipEngineComponents._halLookupService = HoldAtLocationImp.GetInstance();
          }
        }
        return ShipEngineComponents._halLookupService;
      }
    }

    public static DataAccessServiceImp DataAccessService
    {
      get
      {
        if (ShipEngineComponents._dataAccessService == null)
        {
          lock (ShipEngineComponents._getInstanceLock)
          {
            FxLogger.LogMessage(FedEx.Gsm.Common.Logging.LogLevel.Info, FxLogger.AppCode_Default, nameof (DataAccessService), "Creating DataAccessService Object");
            ShipEngineComponents._dataAccessService = DataAccessServiceImp.GetInstance();
          }
        }
        return ShipEngineComponents._dataAccessService;
      }
    }

    public static RouteServiceImp RouteService
    {
      get
      {
        if (ShipEngineComponents._routeService == null)
        {
          lock (ShipEngineComponents._getInstanceLock)
          {
            FxLogger.LogMessage(FedEx.Gsm.Common.Logging.LogLevel.Info, FxLogger.AppCode_Default, nameof (RouteService), "Creating RouteService Object");
            ShipEngineComponents._routeService = RouteServiceImp.GetInstance();
          }
        }
        return ShipEngineComponents._routeService;
      }
    }
  }
}
