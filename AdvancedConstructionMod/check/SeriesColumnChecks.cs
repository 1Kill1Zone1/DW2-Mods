using AdvancedConstructionMod;
using DistantWorlds.Types;
using DistantWorlds.UI;
using HarmonyLib;
using Stride.Core.Mathematics;
using System.Runtime.CompilerServices;
using Stride.Graphics;
using UiChecks;

internal static partial class Checks
{
    private static void SeriesColumnChecks(Galaxy galaxy,Empire empire)
    {
        Console.WriteLine("Native checks: Series column and stock Designs header bindings.");
        Require(DesignsSeriesColumn.RoleWithoutShownSpec("Frigate - Picket","Picket")=="Frigate" &&
            DesignsSeriesColumn.RoleWithoutShownSpec("Frigate - Picket",null)=="Frigate - Picket" &&
            DesignsSeriesColumn.RoleWithoutShownSpec("Frigate - Other","Picket")=="Frigate - Other","only remove the specialization displayed in Spec from Role");
        using var metrics=new FontMetrics();using var ui=new NativeUi(galaxy);
        var baseline=Design.Fields.Select(f=>(f.Name,f.Size,f.ShowColumnHeader,f.HorizontalPadding)).ToArray();
        var dialog=new DesignsDialog();dialog.BindGalaxy(galaxy,empire);
        dialog.SetSizeAndPosition(new(1400,700),new(137,89));
        dialog.Initialize(FontSize.Normal,FontSize.Large,null!);
        AccessTools.Method(typeof(DesignsDialog),"BindDesignsList").Invoke(dialog,[null]);
        var layouts=(ConditionalWeakTable<DesignsDialog,DesignsSeriesColumn.Layout>)AccessTools.Field(typeof(DesignsSeriesColumn),"Dialogs").GetValue(null)!;
        Require(layouts.TryGetValue(dialog,out var layout),"actual native Designs initialization attaches scoped Series layout");
        var hit=layout!.HitFields;var native=layout.NativeFields;
        int name=hit.FindIndex(f=>f.Name=="Name");
        Require(hit[name+1].Name==DesignsSeriesColumn.FieldName&&hit[name+2].Name==DesignsSeriesColumn.SpecField&&hit[name+3].Name=="Role","visible and hit-test field order is Name, Series, Spec, Role");
        Require(Math.Abs(hit.GetTotalWidth()-Design.Fields.GetTotalWidth())<.001,"Series preserves total native row width");
        Require(layout.Name.Size==Design.Fields.Single(f=>f.Name=="Name").Size,"Series no longer takes width from the native project Name");
        foreach(var field in Design.Fields.Where(f=>f.Name!="Name"))
            Require(Math.Abs(hit.GetFieldPosition(field.Name)-native.GetFieldPosition(field.Name))<.001,"header and native row positions agree: "+field.Name);
        var fields=hit;DesignsSeriesColumn.BeforeDraw(ref fields);Require(ReferenceEquals(fields,native),"only registered split fields use positional native draw layout");
        fields=Design.Fields;DesignsSeriesColumn.BeforeDraw(ref fields);Require(ReferenceEquals(fields,Design.Fields),"other native design lists keep their own field layout");
        Require(ReferenceEquals(AccessTools.Field(typeof(ScrollableList),"_Fields").GetValue(dialog.DesignsList),hit),"real Designs list owns instance-only hit fields");
        Require(dialog.DesignsList.Items.All(item=>item is Design)&&dialog.DesignsList.Items.Cast<Design>().All(d=>ReferenceEquals(galaxy.Designs.GetById(d.DesignId),d)),"native selections retain original Design instances for Edit and Upgrade");
        Require(layout.NameHeader?.Visible==true&&layout.SeriesHeader?.Visible==true,"both native Name and Series header buttons are visible");
        var role=dialog.ColumnHeadings.Controls.OfType<DWButton>().Single(b=>b.ExtraClickData is string value&&value=="Role");
        float roleX=role.Position.X,nameWidth=layout.NameHeader!.Size.X;int headings=dialog.ColumnHeadings.Controls.Count;
        for(int i=0;i<3;i++)DesignsSeriesColumn.Attach(dialog);
        Require(layout.NameHeader.Size.X==nameWidth&&role.Position.X==roleX&&dialog.ColumnHeadings.Controls.Count==headings,"rebind does not shrink Name repeatedly or move stock sort buttons");
        var nameRect=layout.NameHeader.CalculateAbsoluteRectangle();var seriesRect=layout.SeriesHeader!.CalculateAbsoluteRectangle();
        Require(nameRect.Right<=seriesRect.Left+1,"Series header cannot click through a full-width Name sort button");
        Require(Math.Abs(nameRect.Y-seriesRect.Y)<.01&&Math.Abs(role.CalculateAbsoluteRectangle().Y-seriesRect.Y)<.01&&
            ReferenceEquals(layout.SeriesHeader.Parent,dialog.ColumnHeadings),"Series shares the native header parent and absolute baseline at a nonzero dialog position");
        Require(layout.SeriesHeader.ButtonColor==layout.NameHeader.ButtonColor&&layout.SeriesHeader.ButtonColorHover==layout.NameHeader.ButtonColorHover&&
            layout.SeriesHeader.ForeColor==layout.NameHeader.ForeColor&&layout.SeriesHeader.FontNormal==layout.NameHeader.FontNormal&&
            layout.SeriesHeader.UseBoldFont==layout.NameHeader.UseBoldFont&&layout.SeriesHeader.TextAlignment==layout.NameHeader.TextAlignment,
            "Series uses native header background, hover color, font and alignment");
        dialog.Visible=true;
        var edge=new Point((int)seriesRect.Right-1,(int)(seriesRect.Y+seriesRect.Height/2));
        var end=new Point(edge.X+13,edge.Y);
        float initial=layout.Series.Size, other=layout.Spec.Size;
        UserInterfaceController.HoveredControl=layout.SeriesHeader;
        UserInterfaceController.DoMouseDown(new GameSettings(),edge,true,empire,false,false);
        UserInterfaceController.DoMouseMove(new GameSettings(),new(13,0),end,true,false);
        UserInterfaceController.DoMouseUp(new GameSettings(),end,true,empire,false,false,.1f);
        Require(layout.Series.Size>initial && Math.Abs(layout.Series.Size+layout.Spec.Size-initial-other)<.01,"drag reallocates space between adjacent headers");
        var restoredLayout=new DesignsSeriesColumn.Layout(dialog);
        Require(restoredLayout.Series.Size==layout.Series.Size,"new Designs layout reads saved width");
        var boundary=layout.SeriesHeader.CalculateAbsoluteRectangle();
        var resetAt=new Point((int)boundary.Right-1,edge.Y);
        for(int click=0;click<2;click++)
        {
            UserInterfaceController.HoveredControl=layout.SeriesHeader;
            UserInterfaceController.DoMouseDown(new GameSettings(),resetAt,true,empire,false,false);
            UserInterfaceController.DoMouseUp(new GameSettings(),resetAt,true,empire,false,false,.1f);
        }
        Require(Math.Abs(initial-layout.Series.Size)<.01,"double click resets column widths");
        var sort=AccessTools.Field(typeof(DesignsDialog),"_SortField");
        foreach(string key in new[]{"Name","Role","ShipHull","BuildCost","MaintenanceCost","DateCreated","Size","Amount","AutoDesignRole","AllowAutoRetrofit","IsObsolete"})
        {
            var button=dialog.ColumnHeadings.Controls.OfType<DWButton>().Single(b=>b.ExtraClickData is string value&&value==key);
            NativeUi.Click(button);Require((string?)sort.GetValue(dialog)==key,"actual native header still selects sort key: "+key);
        }
        var registry=ConstructionCampaigns.Get(galaxy,empire);Require(registry.TryGet(empire,out var snapshot,out _),"Series text reads current committed metadata");
        var classified=empire.Designs.First(d=>snapshot.DesignClasses.ContainsKey(d.DesignId));string id=snapshot.DesignClasses[classified.DesignId];
        Require(layout.Text(classified,galaxy,empire)==snapshot.Classes[id],"series column displays committed own project value");
        dialog.DesignsList.SelectedItems=[classified];layout.RefreshDetails();
        Require(layout.Details?.Visible==true&&layout.Details.Text.Contains(classified.Name)&&layout.Details.Text.Contains(snapshot.Classes[id]),"visible detail area contains the complete selected project and series names");
        var staged=snapshot.Clone();staged.Classes[id]="Uncommitted rename";
        Require(layout.Text(classified,galaxy,empire)!=staged.Classes[id],"staged rename never leaks into the Designs column");
        Require(registry.TryCommit(empire,staged,out _)&&layout.Text(classified,galaxy,empire)=="Uncommitted rename","committed rename refreshes without stale row cache");
        Require(registry.TryRestore(empire,snapshot,out _),"restore isolated name after column rename case");
        SeriesSortChecks(dialog,layout,galaxy,empire,snapshot);
        var empty=empire.Designs.First(d=>!snapshot.DesignClasses.ContainsKey(d.DesignId));Require(layout.Text(empty,galaxy,empire)=="—","unassigned designs show dash");
        var foreign=galaxy.Empires.First(e=>e!=empire&&e.Designs.Count>0);var foreignDesign=foreign.Designs.First();
        Require(layout.Text(foreignDesign,galaxy,empire)=="—"&&layout.Text(foreignDesign,galaxy,foreign)=="—","foreign project and foreign dialog context reveal no private series metadata");
        var font=UserInterfaceHelper.SelectFont(FontSize.Normal,false);string longName=new string('Ж',78)+"🚀";
        string fit=(string)AccessTools.Method(typeof(DesignsSeriesColumn),"Fit").Invoke(null,[longName,font,80f])!;
        Require(font.MeasureString(fit).X<=80&&fit.EndsWith("…")&&!fit.Any(char.IsHighSurrogate),"long series/name values fit the actual cell width without splitting a surrogate pair");
        string originalField=(string?)sort.GetValue(dialog)??"Name";
        AccessTools.Method(typeof(DWButtonStrip),"DoLayout").Invoke(dialog.ColumnHeadings,null);
        Require(layout.NameHeader.Size.X==nameWidth&&layout.SeriesHeader.Visible&&(string?)sort.GetValue(dialog)==originalField,"native header relayout preserves split geometry and current sort");
        SeriesRowDrawChecks(dialog,layout,galaxy,empire,classified,empty);
        Require(baseline.SequenceEqual(Design.Fields.Select(f=>(f.Name,f.Size,f.ShowColumnHeader,f.HorizontalPadding))),"global native Design.Fields never mutated");
    }

    private static void SeriesSortChecks(DesignsDialog dialog,DesignsSeriesColumn.Layout layout,Galaxy galaxy,Empire empire,ConstructionSnapshot original)
    {
        var fixtures=empire.Designs.Where(d=>d.GetShipHull(galaxy)!=null).OrderBy(d=>d.DesignId).Take(6).ToArray();
        Require(fixtures.Length==6,"native sorting fixture has six own designs");
        var names=fixtures.Select(d=>d.Name).ToArray();var obsolete=fixtures.Select(d=>d.Obsolete).ToArray();
        var source=dialog.SourceDesigns;var status=AccessTools.Field(typeof(DesignsDialog),"_FilterStatusIndex");var type=AccessTools.Field(typeof(DesignsDialog),"_FilterTypeIndex");
        var oldStatus=status.GetValue(dialog);var oldType=type.GetValue(dialog);var registry=ConstructionCampaigns.Get(galaxy,empire);
        try
        {
            var state=original.Clone();string a=ConstructionGraph.CreateClass(state,"ACM Sort A"),z=ConstructionGraph.CreateClass(state,"ACM Sort Z");
            for(int i=0;i<fixtures.Length;i++){fixtures[i].Name=new[]{"Tie","Tie","Beta","Alpha","A missing","Z missing"}[i];fixtures[i].Obsolete=false;state.DesignClasses.Remove(fixtures[i].DesignId);}
            state.DesignClasses[fixtures[0].DesignId]=state.DesignClasses[fixtures[1].DesignId]=a;
            state.DesignClasses[fixtures[2].DesignId]=state.DesignClasses[fixtures[3].DesignId]=z;
            Require(registry.TryCommit(empire,state,out _),"commit isolated sort fixture metadata");
            var subset=new DesignList();subset.AddRange(fixtures.Reverse().ToArray());dialog.SetSourceDesigns(subset);status.SetValue(dialog,8);type.SetValue(dialog,0);
            AccessTools.Field(typeof(DesignsDialog),"_SortField").SetValue(dialog,"Name");
            AccessTools.Method(typeof(DesignsDialog),"BindDesignsList").Invoke(dialog,[null]);
            dialog.DesignsList.SelectedItems=[fixtures[2]];
            NativeUi.Click(layout.SeriesHeader!);
            int[] Ids()=>dialog.DesignsList.Items.Cast<Design>().Select(d=>d.DesignId).ToArray();
            int[] Expected(params int[] order)=>order.Select(i=>fixtures[i].DesignId).ToArray();
            Require(Ids().SequenceEqual(Expected(0,1,3,2,4,5)),"native Series header sorts ASC, missing last, then Name and DesignId");
            Require(dialog.DesignsList.SelectedItems.Contains(fixtures[2]),"Series sorting preserves selected original Design");
            Require(ReferenceEquals(layout.SeriesHeader!.Image,DrawingHelper.DownArrow),"ASC Series header uses the native direction image");
            NativeUi.Click(layout.SeriesHeader);
            Require(Ids().SequenceEqual(Expected(3,2,0,1,4,5)),"native Series header sorts DESC with missing still last and stable within-series Name/ID order");
            Require(ReferenceEquals(layout.SeriesHeader.Image,DrawingHelper.UpArrow),"DESC Series header uses the native direction image");
            fixtures[3].Obsolete=true;status.SetValue(dialog,4);
            AccessTools.Method(typeof(DesignsDialog),"BindDesignsList").Invoke(dialog,[null]);
            Require(Ids().SequenceEqual(Expected(2,0,1,4,5)),"native active-only filter remains effective under Series sorting");
            status.SetValue(dialog,8);NativeUi.Click(layout.NameHeader!);
            Require(Ids().Take(2).SequenceEqual(Expected(4,3)),"switching from Series to native Name sorting reuses stock sort behavior");
        }
        finally
        {
            for(int i=0;i<fixtures.Length;i++){fixtures[i].Name=names[i];fixtures[i].Obsolete=obsolete[i];}
            if(!registry.TryRestore(empire,original,out var error))throw new Exception(error);
            dialog.SetSourceDesigns(source);status.SetValue(dialog,oldStatus);type.SetValue(dialog,oldType);
            AccessTools.Field(typeof(DesignsDialog),"_SortField").SetValue(dialog,"Name");
            AccessTools.Method(typeof(DesignsDialog),"BindDesignsList").Invoke(dialog,[null]);
        }
    }

    private static readonly List<(string Text,Vector2 Position,Color Color)> SeriesDraws=[];
    private static bool CaptureSeriesDraw(object[] __args)
    {SeriesDraws.Add(((string)__args[1],(Vector2)__args[5],(Color)__args[3]));return false;}
    private static bool SkipSeriesLine()=>false;
    private static bool SkipSeriesHullImage(ref Texture __result){__result=null!;return false;}
    private static void SeriesRowDrawChecks(DesignsDialog dialog,DesignsSeriesColumn.Layout layout,Galaxy galaxy,Empire empire,Design assigned,Design empty)
    {
        var hooks=new Harmony("acm.check.series-render");
        var batch=(SpriteBatch)RuntimeHelpers.GetUninitializedObject(typeof(SpriteBatch));GC.SuppressFinalize(batch);
        try
        {
            foreach(var method in AccessTools.GetDeclaredMethods(typeof(DrawingHelper)).Where(m=>m.Name=="DrawStringDropShadow"&&m.GetParameters().Length is 6 or 8))
                hooks.Patch(method,prefix:new HarmonyMethod(typeof(Checks),nameof(CaptureSeriesDraw)));
            foreach(var method in AccessTools.GetDeclaredMethods(typeof(DrawingHelper)).Where(m=>m.Name=="DrawLine"))
                hooks.Patch(method,prefix:new HarmonyMethod(typeof(Checks),nameof(SkipSeriesLine)));
            hooks.Patch(AccessTools.Method(typeof(ShipHull),"GetImage"),prefix:new HarmonyMethod(typeof(Checks),nameof(SkipSeriesHullImage)));
            var font=UserInterfaceHelper.SelectFont(FontSize.Normal,false);var point=new Vector2(100,200);
            foreach(var design in new[]{assigned,empty})foreach(bool inView in new[]{false,true})
            {
                SeriesDraws.Clear();var tips=new HoverTipList();
                ((IListDrawable)design).DrawItem(null!,batch,dialog.DesignsList.DrawDetailType,new Point(),point,1400,4,font,font,font,font,
                    Color.White,Color.Black,1,1,1,layout.HitFields,galaxy,empire,null!,ref tips,out _,out _,out _,out _,inView);
                foreach(var tip in tips.Where(t=>t.Title is "Name" or "Series" or "ShipHull" or "Role"))CheckTooltip(tip,"design-row "+tip.Title+" tooltip");
                float x=point.X+UserInterfaceHelper.CalculateScaledValue(layout.HitFields.GetFieldPosition(DesignsSeriesColumn.FieldName)+layout.Series.HorizontalPadding);
                float specX=point.X+UserInterfaceHelper.CalculateScaledValue(layout.HitFields.GetFieldPosition(DesignsSeriesColumn.SpecField)+layout.Spec.HorizontalPadding);
                Require(layout.SpecHeader?.Text=="Spec"&&SeriesDraws.Any(d=>d.Text==(layout.Specialization(design,galaxy,empire)??"\u2014")&&Math.Abs(d.Position.X-specX)<.01),"separate Spec header and row align");
                string expected=DesignsSeriesColumn.Fit(layout.Text(design,galaxy,empire),font,
                    UserInterfaceHelper.CalculateScaledValue(layout.Series.Size-layout.Series.HorizontalPadding-4));
                Require(SeriesDraws.Any(d=>d.Text==expected&&Math.Abs(d.Position.X-x)<.01&&d.Position.Y==point.Y&&d.Color==Color.White),
                    "actual native DrawItem emits Series/dash on the Name baseline, considerInView="+inView);
            }
        }
        finally{hooks.UnpatchAll(hooks.Id);SeriesDraws.Clear();}
    }
}
