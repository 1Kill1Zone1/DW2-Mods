using DistantWorlds.Types;
using DistantWorlds.UI;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Rendering;

namespace AdvancedConstructionMod;

// Stable rows and drafts belong to this window. Only the separate confirmation issues orders.
internal sealed class SelectedRetrofitWindow : NativeWindow
{
    internal readonly Galaxy Galaxy;
    internal readonly Empire Empire;
    internal readonly Ship[] Ships;
    internal readonly HashSet<int> Selected=[];
    internal readonly Dictionary<int,SelectedRetrofitPlan> Plans=[];
    private readonly Dictionary<int,string> results=[];
    private readonly ScrollableList shipsList=new();
    private readonly DWTextBox search;
    private readonly DWButton seriesButton,review,assign,showDetails;
    private readonly DWLabel count;
    private readonly NativeTextPager details;
    private readonly ListDrawableFieldList shipFields=new();
    private readonly float[] shipWidths;
    private readonly Row[] rows;
    private readonly HashSet<IListDrawable> highlighted=[];
    private ConstructionSnapshot state;
    private string? series;
    private Row? detailRow;
    private string filterKey="";
    private readonly Dictionary<string,float> viewports=[];
    private string lastDetails="";

    internal SelectedRetrofitWindow(Galaxy galaxy,Empire empire,IEnumerable<Ship> ships,bool preselect)
        :base("ACM.SelectedRetrofit","RETROFIT TO SERIES",1280,850,true)
    {
        Galaxy=galaxy;Empire=empire;Campaign=galaxy;
        Ships=ships.Where(s=>s!=null).DistinctBy(s=>s.ShipId).ToArray();
        if(preselect)Selected.UnionWith(Ships.Select(s=>s.ShipId));
        if(!ConstructionCampaigns.Get(galaxy,empire).TryGet(empire,out state!,out var error))throw new InvalidDataException(error);
        rows=Ships.Select(s=>new Row(this,s)).ToArray();
        float width=Panel.Size.X-S(24);
        count=Label("ACM.RetrofitCount","",S(12),S(40),width,S(26));
        search=TextBox("ACM.RetrofitSearch",S(12),S(76),width-S(500));
        search.PromptTextWhenEmpty="Search ship, project, Series or role";
        Button("ACM.RetrofitSearchApply","Search",Panel.Size.X-S(500),S(76),S(88),BindRows);
        seriesButton=Button("ACM.RetrofitSeries","All current Series",Panel.Size.X-S(400),S(76),S(248),ChooseFilter);
        Button("ACM.RetrofitRefresh","Refresh",Panel.Size.X-S(140),S(76),S(128),()=>{Refresh();BindRows();});
        assign=Button("ACM.RetrofitAssign","Assign Series...",S(12),S(116),S(190),ChooseSeries);
        Button("ACM.RetrofitClear","Clear checks",S(214),S(116),S(140),()=>{Selected.Clear();Refresh();});
        Button("ACM.RetrofitClearAssignment","Clear assignment",S(366),S(116),S(190),()=>
        {
            foreach(int id in Selected){Plans.Remove(id);results.Remove(id);}Refresh();BindRows();
        });
        showDetails=Button("ACM.RetrofitShowDetails","Details",Panel.Size.X-S(140),S(116),S(128),OpenDetails);
        shipWidths=[S(34),(width-S(34))*.18f,(width-S(34))*.25f,(width-S(34))*.29f,(width-S(34))*.28f];
        string[] keys=["Select","Ship","Current","Assigned","State"],captions=["[ ]","Ship / role","Current project / Series","Assigned project / Series","Status"];
        float x=S(12);
        for(int i=0;i<keys.Length;i++){shipFields.Add(new(keys[i],UserInterfaceHelper.CalculateScaledValueInverse(shipWidths[i]),true,4));Label("ACM.RetrofitShips.Header."+keys[i],captions[i],x,S(158),shipWidths[i]-S(4),S(30));x+=shipWidths[i];}
        shipsList.Name="ACM.RetrofitShips";shipsList.PostScene=true;shipsList.Initialize(FontSize.Normal,FontSize.Normal,null!);shipsList.Visible=true;
        shipsList.SetSizeAndPosition(new(width,Math.Max(S(60),Panel.Size.Y-S(444))),new(S(12),S(190)));
        shipsList.IsSelectable=true;shipsList.AllowMultiSelect=false;shipsList.BindGalaxy(Galaxy,Empire);Panel.AddControl(shipsList);
        details=new NativeTextPager(this,"ACM.RetrofitDetails",S(12),Panel.Size.Y-S(246),width,S(150),S(26),true,hideSinglePage:true);
        Status=Label("ACM.RetrofitStatus","",S(12),Panel.Size.Y-S(86),width,S(28));
        review=Button("ACM.RetrofitReview","Review all assigned plans...",S(12),Panel.Size.Y-S(42),S(330),Review);
        Button("ACM.RetrofitCancel","Cancel",Panel.Size.X-S(132),Panel.Size.Y-S(42),S(120),Close);
        Refresh();BindRows();
    }
    internal string SeriesName(int id)=>SeriesTitle(state.DesignClasses.GetValueOrDefault(id));
    internal string SeriesTitle(string? id)=>id==null?"Unclassified":state.Classes.GetValueOrDefault(id,"Missing Series");
    private void Refresh()
    {
        if(ConstructionCampaigns.Get(Galaxy,Empire).TryGet(Empire,out var latest,out var error))state=latest;
        else Message(error,true);
        foreach(var row in rows)row.Refresh();
        count.Text=$"{Selected.Count} checked | {Plans.Count} assigned plans | {Ships.Length} ships";
        assign.Enabled=Selected.Count>0;review.Enabled=Plans.Count>0;
        showDetails.Enabled=detailRow!=null;
        if(detailRow!=null)lastDetails="";
        details.SetText(lastDetails);
    }
    private void BindRows()
    {
        string query=search.Text?.Trim()??"";
        string key=(series??"")+"\n"+query;
        var visible=rows.Where(r=>(series==null||state.DesignClasses.GetValueOrDefault(r.Ship.DesignId)==series)&&
            (query.Length==0||r.SearchText.Contains(query,StringComparison.OrdinalIgnoreCase))).Cast<IListDrawable>().ToList();
        // Native BindData calls Clear (including its viewport). Avoid it for selection/refresh when
        // the actual visible set is unchanged; restore the same row objects and offset otherwise.
        if(shipsList.Items==null||!shipsList.Items.SequenceEqual(visible))
        {
            viewports[filterKey]=shipsList.ScrollOffset;
            float offset=viewports.GetValueOrDefault(key,shipsList.ScrollOffset);filterKey=key;
            foreach(var item in shipsList.SelectedItems)highlighted.Add(item);
            shipsList.BindData(visible,ShipClicked,null!,null!,shipFields);
            shipsList.SelectedItems.Clear();shipsList.SelectedItems.AddRange(visible.Where(highlighted.Contains));
            shipsList.ScrollOffset=offset;shipsList.ScrollToSelectedOnce=false;
        }
        seriesButton.Text=series==null?"All current Series":"Current: "+SeriesTitle(series);
    }
    private void ChooseFilter()
    {
        var choices=new List<(string,Action)>{("All current Series",()=>{series=null;BindRows();})};
        choices.AddRange(state.Classes.OrderBy(p=>p.Value).Select(p=>(p.Value,(Action)(()=>{series=p.Key;BindRows();}))));
        _=new SeriesChoiceWindow("FILTER SHIP TABLE",choices,Reopen,"Filter by the ship's current Series. Hidden checks and plans are retained.","Filtering issues no orders."){Campaign=Galaxy};
    }
    private void ChooseSeries()
    {
        Refresh();var picked=Ships.Where(s=>Selected.Contains(s.ShipId)).ToArray();
        var choices=new List<(string,Action)>();
        foreach(var pair in state.Classes.OrderBy(p=>p.Value))
        {
            var plans=picked.Select(s=>SelectedRetrofitPlan.Resolve(Galaxy,Empire,s,pair.Key)).ToArray();
            if(!plans.Any(p=>p.Target!=null))continue;
            string roles=string.Join(", ",plans.Where(p=>p.Target!=null).Select(p=>p.Target!.GetRole(Galaxy).ToString()).Distinct());
            string missing=string.Join(", ",plans.Where(p=>p.Target==null).Select(p=>p.Ship.GetDesign()?.GetRole(Galaxy).ToString()??"unavailable").Distinct());
            string id=pair.Key;
            choices.Add((pair.Value+" | "+roles+(missing.Length==0?"":" | no compatible: "+missing),()=>AssignSeries(id)));
        }
        _=new SeriesChoiceWindow("ASSIGN SERIES TO CHECKED SHIPS",choices,Reopen,
            "Latest active, researched, native-compatible project per ship role. Shared pinned / No target settings stay unchanged.\nChecked roles: "+string.Join(", ",picked.Select(s=>s.GetDesign()?.GetRole(Galaxy).ToString()??"unavailable").Distinct()),
            "No orders yet. Clear checks to select the next group."){Campaign=Galaxy};
    }
    internal void AssignSeries(string id)
    {
        foreach(var ship in Ships.Where(s=>Selected.Contains(s.ShipId)))
        {Plans[ship.ShipId]=SelectedRetrofitPlan.Resolve(Galaxy,Empire,ship,id);results.Remove(ship.ShipId);}
        lastDetails="Assigned "+SeriesTitle(id)+" to "+Selected.Count+" checked ships. Previous plans on other ships are retained.\n"+
            string.Join("\n",Ships.Where(s=>Selected.Contains(s.ShipId)).Select(s=>s.Name+" -> "+(Plans[s.ShipId].Target?.Name??"No compatible project")+": "+(Plans[s.ShipId].Reason(Galaxy,Empire)??"Ready")));
        detailRow=null;Refresh();BindRows();
    }
    private void ShipClicked(IListDrawable item,string field,Vector2 ratio,bool left,bool add,ShipList ships,TroopList troops,List<IListDrawable> range)
    {
        if(!left||item is not Row row)return;
        if(field!="State"){if(!Selected.Remove(row.Ship.ShipId))Selected.Add(row.Ship.ShipId);}
        shipsList.SelectedItems.Clear();shipsList.SelectedItems.Add(row);shipsList.ScrollToSelectedOnce=false;
        highlighted.Clear();highlighted.Add(row);detailRow=row;Refresh();
        if(field=="State")OpenDetails();
    }
    private void OpenDetails()
    {
        if(detailRow==null || !ReferenceEquals(Active,this))return;
        detailRow.Refresh();
        _=new ShipDetails(this,detailRow);
    }
    private sealed class ShipDetails:NativeWindow
    {
        private readonly SelectedRetrofitWindow parent;
        internal ShipDetails(SelectedRetrofitWindow parent,Row row):base("ACM.RetrofitShipDetails",row.Ship.Name,720,350,true)
        {
            this.parent=parent;Campaign=parent.Galaxy;
            var body=new NativeTextPager(this,"ACM.ShipDetails",S(16),S(48),Panel.Size.X-S(32),Panel.Size.Y-S(116),S(28),true,true);
            body.SetText(row.Description);
            Button("ACM.ShipDetailsBack","Back",Panel.Size.X-S(140),Panel.Size.Y-S(42),S(124),Close);
        }
        internal override void Close(){bool active=ReferenceEquals(Active,this);base.Close();if(active)parent.Reopen();}
    }
    private void Review()
    {
        if(!ReferenceEquals(Active,this)||Plans.Count==0)return;
        try
        {
            // Re-resolve only at explicit Review; the fresh exact projects are shown for confirmation.
            foreach(var plan in Plans.Values.ToArray())Plans[plan.Ship.ShipId]=SelectedRetrofitPlan.Resolve(Galaxy,Empire,plan.Ship,plan.Series);
            Refresh();_=new SelectedRetrofitConfirmation(this,Plans.Values.ToArray());
        }
        catch(Exception ex)
        {
            if(!ReferenceEquals(Active,this))Active?.Close();Reopen();
            Message("Review unavailable: "+ex.GetBaseException().Message+". Plans retained; refresh and review again.",true);
        }
    }
    internal void Result(SelectedRetrofitResult[] outcomes,string? error=null)
    {
        foreach(var outcome in outcomes)
        {
            results[outcome.ShipId]=outcome.Text;
            if(outcome.Issued){Plans.Remove(outcome.ShipId);Selected.Remove(outcome.ShipId);}
        }
        lastDetails=error??string.Join("\n",outcomes.Select(r=>r.Text));detailRow=null;Reopen();Refresh();BindRows();
        Message(error??$"{outcomes.Count(r=>r.Issued)} orders issued. {Plans.Count} unissued plans retained; review explicitly to retry.",error!=null);
    }
    private sealed class Row(SelectedRetrofitWindow window,Ship ship):IListDrawable
    {
        internal Ship Ship=>ship;
        private string current="",assigned="",status="",description="";
        internal string Description=>description;
        internal string SearchText=>ship.Name+" "+ship.Role+" "+current+" "+assigned;
        internal void Refresh()
        {
            current=(ship.GetDesign()?.Name??"Unavailable")+" / "+window.SeriesName(ship.DesignId);
            window.Plans.TryGetValue(ship.ShipId,out var plan);
            assigned=plan==null?"Not assigned":(plan.Target?.Name??"No compatible project")+" / "+window.SeriesTitle(plan.Series);
            try{status=plan==null?SelectedRetrofit.ShipReason(window.Galaxy,window.Empire,ship)??"No assignment":plan.Reason(window.Galaxy,window.Empire)??"Ready";}
            catch(Exception ex){status="State unavailable: "+ex.GetBaseException().Message;}
            if(window.results.TryGetValue(ship.ShipId,out var result))status=result+" | "+status;
            description=$"Role: {ship.Role}\nCurrent: {current} [#{ship.DesignId}]\nAssigned: {assigned}"+
                (plan?.Target==null?"":$" [#{plan.Target.DesignId}] | Hull: {plan.Target.GetShipHullName(window.Galaxy)}")+"\nStatus: "+status;
        }
        public float DrawItem(RenderDrawContext context,SpriteBatch batch,DrawDetailType type,Point mouse,Vector2 position,float width,float gap,SpriteFont font,SpriteFont boldFont,SpriteFont majorFont,SpriteFont smallFont,Color color,Color shadow,float opacity,float saturation,float contrast,ListDrawableFieldList fields,Galaxy galaxy,Empire empire,object extra,ref HoverTipList tips,out GameTask task,out GameTask task2,out bool remove,out bool smallTask,bool considerInView)
        {
            task=null!;task2=null!;remove=false;smallTask=false;float height=(font==null?16:font.GetTotalLineSpacing(font.Size))+window.S(8);
            if(batch!=null&&font!=null&&considerInView)
            {
                string[] cells=[window.Selected.Contains(ship.ShipId)?"[x]":"[ ]",ship.Name+" / "+ship.Role,current,assigned,status];float x=position.X;
                for(int i=0;i<cells.Length;i++){DrawingHelper.DrawStringDropShadow(batch,DesignsSeriesColumn.Fit(cells[i],font,window.shipWidths[i]-window.S(8)),font,window.Selected.Contains(ship.ShipId)?ColorHelper.AccentColor:color,shadow,new(x+window.S(4),position.Y));x+=window.shipWidths[i];}
            }
            return height+gap;
        }
    }
}

internal sealed class SelectedRetrofitConfirmation : NativeWindow
{
    private readonly SelectedRetrofitWindow parent;
    private readonly SelectedRetrofitRequest[] accepted;
    private readonly DWButton send;
    private bool submitted;
    internal SelectedRetrofitConfirmation(SelectedRetrofitWindow parent,SelectedRetrofitPlan[] plans):base("ACM.RetrofitConfirm","CONFIRM INDIVIDUAL RETROFITS",1060,720,true)
    {
        this.parent=parent;Campaign=parent.Galaxy;
        var rows=plans.Select(p=>
        {
            string? reason;SelectedRetrofitRequest? request=null;
            try{reason=p.Reason(parent.Galaxy,parent.Empire);if(reason==null)request=SelectedRetrofitRequest.Capture(p,out reason);}
            catch(Exception ex){reason="Ship state unavailable during review: "+ex.GetBaseException().Message;}
            return (Plan:p,Reason:reason,Request:request);
        }).ToArray();
        accepted=rows.Where(r=>r.Request!=null).Select(r=>r.Request!).ToArray();
        var text=new NativeTextPager(this,"ACM.RetrofitConfirmation",S(12),S(42),Panel.Size.X-S(24),Panel.Size.Y-S(176),S(26),true);
        string summary=accepted.Length==0?"No ships are ready. Nothing will be sent or charged. Back retains every plan.":$"{accepted.Length} ships ready; {rows.Length-accepted.Length} skipped. Confirm the exact projects below.";
        var estimates=accepted.GroupBy(r=>r.Plan.Target!.DesignId).Select(group=>
            "Target: "+group.First().Plan.Target!.Name+" [#"+group.Key+"] / "+parent.SeriesTitle(group.First().Plan.Series)+"\n"+
            SelectedRetrofit.Estimate(parent.Galaxy,parent.Empire,group.Select(r=>r.Plan.Ship),group.First().Plan.Target!));
        text.SetText(summary+"\n"+string.Join("\n",rows.Select(r=>r.Plan.Ship.Name+" | "+r.Plan.Ship.GetDesign()?.Name+" -> "+(r.Plan.Target?.Name??"No compatible project")+" / "+parent.SeriesTitle(r.Plan.Series)+": "+(r.Reason==null?"SEND":"UNCHANGED - "+r.Reason)))+"\n"+string.Join("\n",estimates));
        Status=Label("ACM.RetrofitConfirmStatus","Latest projects, ownership, current orders and native readiness are checked again on Send. Skipped plans remain for explicit retry.",S(12),Panel.Size.Y-S(122),Panel.Size.X-S(24),S(58));
        send=Button("ACM.RetrofitSend",accepted.Length==0?"No eligible ships":$"Send {accepted.Length} ships to {accepted.Select(r=>r.Plan.Target!.DesignId).Distinct().Count()} exact targets",S(12),Panel.Size.Y-S(52),Panel.Size.X-S(164),Send,S(36));send.Enabled=accepted.Length>0;
        Button("ACM.RetrofitBack","Back",Panel.Size.X-S(140),Panel.Size.Y-S(52),S(128),Close,S(36));
    }
    private void Send()
    {
        if(submitted||accepted.Length==0||!ReferenceEquals(Active,this))return;
        submitted=true;send.Enabled=false;SelectedRetrofitResult[] result=[];string? error=null;
        try{result=SelectedRetrofit.Send(parent.Galaxy,parent.Empire,accepted);}
        catch(Exception ex){error="Command stopped: "+ex.GetBaseException().Message+". Review current ship states before retrying; accepted native orders remain active.";}
        base.Close();parent.Result(result,error);
    }
    internal override void Close(){bool active=ReferenceEquals(Active,this);base.Close();if(active)parent.Reopen();}
}
