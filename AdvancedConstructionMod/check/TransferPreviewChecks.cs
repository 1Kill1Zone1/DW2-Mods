using AdvancedConstructionMod;
using DistantWorlds.Types;
using DistantWorlds.UI;
using HarmonyLib;
using UiChecks;

internal static partial class Checks
{
    private static void TransferPreviewChecks(Galaxy galaxy,Empire empire,Design source,Design destination)
    {
        Console.WriteLine("Native checks: transfer preview with sparse and stale live ship lists.");
        var registry=ConstructionCampaigns.Get(galaxy,empire);
        Require(registry.TryGet(empire,out var state,out _),"transfer preview reads current metadata");
        string sourceSeries=state.DesignClasses[source.DesignId],destinationSeries=state.DesignClasses[destination.DesignId];
        var originals=galaxy.Ships.Where(s=>s!=null).ToArray();
        var unchanged=originals.ToDictionary(s=>s.ShipId,ShipBytes);
        string metadata=Serialized(Read(Storage(galaxy,empire.EmpireId))!);
        var designFlags=empire.Designs.Where(d=>d!=null).ToDictionary(d=>d.DesignId,d=>(d.Obsolete,d.UpgradeDesignId,d.AllowAutoRetrofit));
        var basis=originals.First(s=>s.EmpireId==empire.EmpireId&&!s.IsDestroyed&&!s.IsBase&&s.GetDesign()!=null);
        int nextId=originals.Max(s=>s.ShipId)+100;
        Ship Copy(Design? design,Empire owner)
        {
            var ship=basis.Clone(galaxy);ship.ShipId=nextId++;ship.Design=design!;ship.Empire=owner;
            ship.Mission=null!;ship.NextMissions=new();return ship;
        }
        var owned=Copy(source,empire);
        var unfinished=Copy(null,empire);
        var foreign=Copy(source,galaxy.Empires.First(e=>e.EmpireId!=empire.EmpireId));
        var stale=Copy(source,empire);
        Require(unfinished.GetDesign()==null,"transfer fixture includes a real native ship with no design");
        var listField=AccessTools.Field(typeof(ShipList),"_List");
        var galaxyList=(IList<Ship>)listField.GetValue(galaxy.Ships)!;
        var empireList=(IList<Ship>)listField.GetValue(empire.Ships)!;
        int expectedCount=originals.Count(s=>!s.IsDestroyed&&s.EmpireId==empire.EmpireId&&s.GetDesign()?.DesignId==source.DesignId)+1;
        using var metrics=new FontMetrics();using var ui=new NativeUi(galaxy);
        // Deliberately disagree: authoritative owned ship only in Galaxy; stale member only in Empire.
        listField.SetValue(galaxy.Ships,galaxyList.Concat(new[]{null!,owned,unfinished,foreign}).ToList());
        listField.SetValue(empire.Ships,empireList.Concat(new[]{null!,unfinished,stale,foreign}).ToList());
        try
        {
            void Click(DWButton button)
            {
                Require(ReferenceEquals(NativeWindow.Active!.Panel.CheckHoveredControl(null!,NativeUi.Center(button)),button),"transfer pointer reaches "+button.Name);
                NativeUi.Click(button);
            }
            DWButton Control(NativeWindow window,string name)=>(DWButton)window.Panel.Controls.Single(c=>c.Name==name);
            void ChooseReceiving(TransferWindow window)
            {
                Click(Control(window,"ACM.TransferDestination"));int pages=0;
                while(true)
                {
                    var picker=NativeWindow.Active!;
                    var choice=picker.Panel.Controls.OfType<DWButton>().FirstOrDefault(c=>c.Visible&&c.Name.StartsWith("ACM.Choice.")&&c.Text==state.Classes[destinationSeries]);
                    if(choice!=null){Click(choice);break;}
                    var next=Control(picker,"ACM.ChoiceNext");Require(next.Enabled&&pages++<100,"receiving series is reachable through native pages");Click(next);
                }
                Require(ReferenceEquals(NativeWindow.Active,window),"receiving-series click returns without a sparse-list crash");
            }
            string ReadPreview(TransferWindow window)
            {
                var label=(DWLabel)window.Panel.Controls.Single(c=>c.Name=="ACM.TransferPreview");
                var previous=Control(window,"ACM.TransferPreview.Previous");int pages=0;
                while(previous.Enabled&&pages++<100)Click(previous);
                Require(!previous.Enabled,"complete preview capture starts at the first native page");
                string text=label.Text;var next=Control(window,"ACM.TransferPreview.Next");pages=0;
                while(next.Enabled&&pages++<100){Click(next);text+="\n"+label.Text;}
                Require(!next.Enabled,"complete preview capture reaches the final native page");
                return string.Join(" ",text.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries));
            }
            foreach(bool whole in new[]{false,true})
            {
                var edit=new EditorSession(galaxy,empire,source,null);string prior=Serialized(edit.Classes);int changes=0;
                var window=new TransferWindow(edit,whole,()=>changes++);
                ChooseReceiving(window);
                string preview=ReadPreview(window);
                Require(preview.Contains(source.Name+" [#"+source.DesignId+"] | ships: "+expectedCount+" |"),"transfer preview counts current owned project ships, ignoring null/designless/foreign/stale entries (whole="+whole+")");
                Require(Control(window,"ACM.TransferUse").Enabled&&Serialized(edit.Classes)==prior&&!edit.Dirty,"valid preview is usable while restoring the exact unstaged editor plan");
                var previewLabel=(DWLabel)window.Panel.Controls.Single(c=>c.Name=="ACM.TransferPreview");string lastPage=previewLabel.Text;
                ChooseReceiving(window);Require(previewLabel.Text==lastPage,"reselecting the same receiving series preserves the current preview page");
                Require(ReadPreview(window)==preview,"reselecting receiving series produces the same complete preview");
                Click(Control(window,"ACM.TransferCancel"));
                Require(changes==0&&Serialized(edit.Classes)==prior&&!edit.Dirty,"Cancel preserves editor plan after sparse-list preview");
                window=new TransferWindow(edit,whole,()=>changes++);ChooseReceiving(window);Click(Control(window,"ACM.TransferUse"));
                var moved=state.DesignClasses.Where(p=>whole?p.Value==sourceSeries:p.Key==source.DesignId).Select(p=>p.Key).ToHashSet();
                Require(changes==1&&edit.Dirty&&moved.All(id=>edit.Classes.DesignClasses[id]==destinationSeries),"Use stages exactly the selected single/whole transfer");
                Require(state.DesignClasses.Where(p=>!moved.Contains(p.Key)).All(p=>edit.Classes.DesignClasses[p.Key]==p.Value),"transfer leaves all unrelated project memberships unchanged");
                Require(Serialized(Read(Storage(galaxy,empire.EmpireId))!)==metadata,"preview, retry, Cancel and Use do not publish Matrix metadata");
            }
            Require(originals.All(s=>ShipBytes(s).SequenceEqual(unchanged[s.ShipId])),"transfer preview and staging leave every original ship byte-for-byte unchanged");
            Require(empire.Designs.Where(d=>d!=null).All(d=>designFlags[d.DesignId]==(d.Obsolete,d.UpgradeDesignId,d.AllowAutoRetrofit)),"transfer preview and staging leave native design flags and paths unchanged");
        }
        finally
        {
            listField.SetValue(galaxy.Ships,galaxyList);listField.SetValue(empire.Ships,empireList);NativeWindow.Active?.Close();
        }
    }
}
