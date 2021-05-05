﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using SpeckleCore;
using Newtonsoft.Json;
using SpeckleCore.Data;
using System.Windows;
using System.Threading;

namespace SpeckleRevit.UI
{
  public partial class SpeckleUiBindingsRevit
  {
    #region overries
    /// <summary>
    /// This function will bake the objects in the given receiver. Behaviour:
    /// 1) Fresh bake: objects are created
    /// 2) Diff bake: old objects are deleted, any overlapping objects (by applicationId) are either edited or left alone if not marked as having been user modified, new objects are created.
    /// </summary>
    /// <param name="args">Serialised client coming from the ui.</param>
    public override void BakeReceiver(string args)
    {
      var client = JsonConvert.DeserializeObject<dynamic>(args);
      var apiClient = new SpeckleApiClient((string)client.account.RestApi) { AuthToken = (string)client.account.Token };
      var task = Task.Run(async () => { await apiClient.IntializeUser(); });
      task.Wait();
      apiClient.ClientType = "Revit";

      //dispatch on the cef window to let progress bar update
      SpeckleWindow.Dispatcher.Invoke(() =>
      {
        NotifyUi("update-client", JsonConvert.SerializeObject(new
        {
          _id = (string)client._id,
          loading = true,
          loadingBlurb = "Getting stream from server..."
        }));
      }, System.Windows.Threading.DispatcherPriority.Background);



      var previousStream = LocalState.FirstOrDefault(s => s.StreamId == (string)client.streamId);
      var stream = apiClient.StreamGetAsync((string)client.streamId, "").Result.Resource;

      InjectScaleInKits(GetScale((string)stream.BaseProperties.units));
      var test = stream.BaseProperties.unitsDictionary;
      if (test != null)
      {
        var secondTest = JsonConvert.DeserializeObject<Dictionary<string, string>>(JsonConvert.SerializeObject(test));
        InjectUnitDictionaryInKits(secondTest);
      }
      else InjectUnitDictionaryInKits(null); // make sure it's not there to potentially muddy the waters on other conversions

      // If it's the first time we bake this stream, create a local shadow copy
      if (previousStream == null)
      {
        previousStream = new SpeckleStream() { StreamId = stream.StreamId, Objects = new List<SpeckleObject>() };
        LocalState.Add(previousStream);
      }

      LocalContext.GetCachedObjects(stream.Objects, (string)client.account.RestApi);
      var payload = stream.Objects.Where(o => o.Type == "Placeholder").Select(obj => obj._id).ToArray();

      apiClient.Stream = stream;
      // TODO: Orchestrate & save in cache afterwards!
      var objects = apiClient.ObjectGetBulkAsync(payload, "").Result.Resources;

      foreach (var obj in objects)
      {
        stream.Objects[stream.Objects.FindIndex(o => o._id == obj._id)] = obj;
      }

      var (toDelete, ToAddOrMod) = DiffStreamStates(previousStream, stream);

      SpeckleWindow.Dispatcher.Invoke(() =>
      {
        NotifyUi("update-client", JsonConvert.SerializeObject(new
        {
          _id = (string)client._id,
          loading = true,
          loadingBlurb = "Deleting " + toDelete.Count() + " objects.",
          objects = stream.Objects
        }));

      }, System.Windows.Threading.DispatcherPriority.Background);



      // DELETION OF OLD OBJECTS
      if (toDelete.Count() > 0)
      {
        Queue.Add(new Action(() =>
      {
        using (Transaction t = new Transaction(CurrentDoc.Document, "Speckle Delete (" + (string)client.streamId + ")"))
        {
          t.Start();
          foreach (var obj in toDelete)
          {
            var myObj = previousStream.Objects.FirstOrDefault(o => o._id == obj._id);
            if (myObj != null)
            {
              var elem = CurrentDoc.Document.GetElement(myObj.Properties["revitUniqueId"] as string);
              CurrentDoc.Document.Delete(elem.Id);
            }
          }
          t.Commit();
        }
      }));
        Executor.Raise();
      }

      // ADD/MOD/LEAVE ALONE EXISTING OBJECTS 

      //if the conversion completely fails, it outputs a speckleerror and it's put in here
      var errors = new List<SpeckleError>();
      //this instead will store errors swallowed by the erroreater class
      Globals.ConversionErrors = new List<SpeckleError>();
      var tempList = new List<SpeckleObject>();
      Queue.Add(new Action(() =>
    {

      using (var t = new Transaction(CurrentDoc.Document, "Speckle Bake"))
      {
        t.Start();

        int i = 0;
        foreach (var mySpkObj in ToAddOrMod)
        {
          SpeckleWindow.Dispatcher.Invoke(() =>
          {
            NotifyUi("update-client", JsonConvert.SerializeObject(new
            {
              _id = (string)client._id,
              loading = true,
              isLoadingIndeterminate = false,
              loadingProgress = 1f * i / ToAddOrMod.Count * 100,
              loadingBlurb = string.Format("Creating/updating objects: {0} / {1}", i, ToAddOrMod.Count)
            }));
          }, System.Windows.Threading.DispatcherPriority.Background);



          object res;

          var failOpts = t.GetFailureHandlingOptions();
          failOpts.SetFailuresPreprocessor(new ErrorEater());
          t.SetFailureHandlingOptions(failOpts);

          try
          {

            res = SpeckleCore.Converter.Deserialise(obj: mySpkObj, excludeAssebmlies: new string[] { "SpeckleCoreGeometryDynamo", "SpeckleCoreGeometryRevit" });

            // The converter returns either the converted object, or the original speckle object if it failed to deserialise it.
            // Hence, we need to create a shadow copy of the baked element only if deserialisation was succesful. 
            if (res is Element)
            {
              // creates a shadow copy of the baked object to store in our local state. 
              var myObject = new SpeckleObject() { Properties = new Dictionary<string, object>() };
              myObject._id = mySpkObj._id;
              myObject.ApplicationId = mySpkObj.ApplicationId;
              myObject.Properties["__type"] = mySpkObj.Type;
              myObject.Properties["revitUniqueId"] = ((Element)res).UniqueId;
              myObject.Properties["revitId"] = ((Element)res).Id.ToString();
              myObject.Properties["userModified"] = false;

              tempList.Add(myObject);
            }

            // TODO: Handle scenario when one object creates more objects. 
            // ie: SpeckleElements wall with a base curve that is a polyline/polycurve
            if (res is System.Collections.IEnumerable)
            {
              int k = 0;
              var xx = ((IEnumerable<object>)res).Cast<Element>();
              foreach (var elm in xx)
              {
                var myObject = new SpeckleObject();
                myObject._id = mySpkObj._id;
                myObject.ApplicationId = mySpkObj.ApplicationId;
                myObject.Properties["__type"] = mySpkObj.Type;
                myObject.Properties["revitUniqueId"] = ((Element)elm).UniqueId;
                myObject.Properties["revitId"] = ((Element)elm).Id.ToString();
                myObject.Properties["userModified"] = false;
                myObject.Properties["orderIndex"] = k++; // keeps track of which elm it actually is

                tempList.Add(myObject);
              }
            }

            //if( res is SpeckleObject || res == null ) failedToBake++;

            if (res is SpeckleError)
              errors.Add(res as SpeckleError);

          }
          catch (Exception e)
          {
            //if(e.Message.Contains("missing"))
            //failedToBake++;
            errors.Add(new SpeckleError { Message = e.Message });
          }


          i++;
        }
        t.Commit();
      }

    }));
      Executor.Raise();


      Queue.Add(new Action(() =>
    {
      SpeckleWindow.Dispatcher.Invoke(() =>
      {
        NotifyUi("update-client", JsonConvert.SerializeObject(new
        {
          _id = (string)client._id,
          loading = true,
          isLoadingIndeterminate = true,
          loadingBlurb = string.Format("Updating shadow state.")
        }));
      }, System.Windows.Threading.DispatcherPriority.Background);



      // set the local state stream's object list, and inject it in the kits, persist it in the doc
      previousStream.Objects = tempList;
      InjectStateInKits();
      using (var t = new Transaction(CurrentDoc.Document, "Speckle State Save"))
      {
        t.Start();
        Storage.SpeckleStateManager.WriteState(CurrentDoc.Document, LocalState);
        t.Commit();
      }

      string errorMsg = "";
      int failedToConvert = errors.Count();

      //other conversion errors that we are catching
      var additionalErrors = GetAndClearConversionErrors();

      if (additionalErrors != null && additionalErrors.Count > 0)
      {
        errors.AddRange(additionalErrors);
      }
      errors.AddRange(Globals.ConversionErrors);

      //remove duplicates
      errors = errors.GroupBy(x => x.Message).Select(x => x.First()).ToList();

      if (errors.Any())
      {
        errorMsg += string.Format("There {0} {1} error{2} ",
         errors.Count() == 1 ? "is" : "are",
         errors.Count(),
         errors.Count() == 1 ? "" : "s");
        if (failedToConvert > 0)
          errorMsg += string.Format("and {0} objects that failed to convert ",
            failedToConvert,
            failedToConvert == 1 ? "" : "s");

        errorMsg += "<nobr>" + Globals.GetRandomSadFace() + "</nobr>";
      }

      //if(errors.Any())
      //{
      //  errorMsg += "" +
      //  //errors += "<v-divider></v-divider>" +
      //  "<v-layout row wrap><v-flex xs12>";
      //  //"<strong>Missing families:</strong>&nbsp;&nbsp;";

      //  foreach( var err in errors)
      //  {
      //    errorMsg += $"<code>{err.Message}</code>&nbsp;";
      //  }

      //  errorMsg += "</v-flex></v-layout>";
      //}

      SpeckleWindow.Dispatcher.Invoke(() =>
      {
        NotifyUi("update-client", JsonConvert.SerializeObject(new
        {
          _id = (string)client._id,
          loading = false,
          isLoadingIndeterminate = true,
          loadingBlurb = string.Format("Done."),
          errorMsg,
          errors
        }));
      }, System.Windows.Threading.DispatcherPriority.Background);



    }));

      Executor.Raise();
    }

    /// <summary>
    /// Shows/hides the zoom on selected objects button
    /// </summary>
    /// <returns></returns>
    public override bool CanSelectObjects()
    {
      return true;
    }

    /// <summary>
    /// Shows/hides the toggle preview icon 
    /// </summary>
    /// <returns></returns>
    public override bool CanTogglePreview()
    {
      return false;
    }

    #endregion
    /// <summary>
    /// Diffs stream objects based on appId + _id non-matching.
    /// </summary>
    /// <param name="Old"></param>
    /// <param name="New"></param>
    /// <returns></returns>
    private (List<SpeckleObject>, List<SpeckleObject>) DiffStreamStates(SpeckleStream Old, SpeckleStream New)
    {
      var ToDelete = Old.Objects.Where(obj =>
     {
       var appIdMatch = New.Objects.FirstOrDefault(x => x.ApplicationId == obj.ApplicationId);
       var idMatch = New.Objects.FirstOrDefault(x => x._id == obj._id);
       return (appIdMatch == null) && (idMatch == null);
     }).ToList();

      var ToModOrAdd = New.Objects;
      return (ToDelete, ToModOrAdd);
    }

    /// <summary>
    /// Gets the scaling factor to/from feet based on the passed in unit string. Used internally by the kits for geometric scaling of primitives.
    /// </summary>
    /// <param name="units">Currently supported: kilometers, meters, centimeters, millimeters, miles, feet, inches.</param>
    /// <returns>the scaling factor.</returns>
    private double GetScale(string units)
    {
      //var units = ( ( string ) stream.BaseProperties.units ).ToLower();
      // TODO: Check unit scales properly
      switch (units.ToLowerInvariant())
      {
        case "kilometers":
          return 3.2808399 * 1000;

        case "meters":
          return 3.2808399;

        case "centimeters":
          return 0.032808399;

        case "millimeters":
          return 0.0032808399;

        case "miles":
          return 5280;

        case "feet":
          return 1;

        case "inches":
          return 0.0833333;

        default:
          return 3.2808399;
      };
    }



  }

}
