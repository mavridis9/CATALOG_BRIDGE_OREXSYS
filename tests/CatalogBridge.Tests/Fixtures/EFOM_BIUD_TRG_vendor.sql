as
declare variable f_insmid type of column insm.insmid;
declare variable f_prlsid type of column insm.prlsid;
declare variable f_defprlsid type of column insm.prlsid;
declare variable f_custid type of column insm.custid;
declare variable f_cusnid type of column insm.cusnid;
declare variable f_syntid type of column insm.syntid;
declare variable f_polsid type of column insm.polsid;
declare variable f_paywid type of column insm.paywid;
declare variable f_paywtipsid type of column payw.paywid;
declare variable f_rvcsid type of column rvcs.rvcsid;
declare variable f_storid type of column rvcs.storid;
declare variable f_useinweborder type of column rvcs.useinweborder;
declare variable f_useinweborder2 type of column rvcs.useinweborder;
declare variable f_latitude type of column cust.maplat;
declare variable f_longtitude type of column cust.maplng;
declare variable f_fulladdress type of column cust.fulladdress;
declare variable f_defrvcsid type of column rvcs.rvcsid;
declare variable f_defdsts type of column efst.ci2;
declare variable f_efstid type of column efst.id;
declare variable f_eficname type of column efic.name;
declare variable f_amount type of column efop.amount;
declare variable f_ptype type of column efop.ptype;   
declare variable f_ordmid type of column insm.ordmid;
-- efod variables
declare variable d_efodid type of column efod.efodid;
declare variable d_storeid type of column efod.storeid;
declare variable d_code type of column efod.code;
declare variable d_name type of column efod.name;
declare variable d_price type of column efod.price;
declare variable d_qty type of column efod.qty;
declare variable d_itmsize type of column efod.itmsize;
declare variable d_sizetype type of column efod.sizetype;
declare variable d_ingredients type of column efod.ingredients;
declare variable d_options type of column efod.options;
declare variable d_itmsid type of column efod.itmsid;
declare variable d_hotcid type of column hotc.hotcid;
declare variable d_coupons type of column efom.comments;
-- insd variables
declare variable d_insdid type of column insd.insdid;
declare variable d_itmvid type of column insd.itmvid;
declare variable d_temp type of column insd.insdid;
declare variable d_string type of column efod.options;
begin
  if (inserting) then
    begin
      if (new.apptype is null) then
       select apptype
         from efst
        where efstid = new.storeid
         into new.apptype;

      if (new.fiscaldate is null) then
       select fiscaldate
         from stor
        where isdefault = 1
         into new.fiscaldate;

      if (new.orderdatetime is null) then
       new.orderdatetime = current_timestamp;

      select usrsid, rvcsid, ci1, ci2
        from efst
       where efstid = new.storeid
        into new.usrsid, :f_rvcsid, :f_defprlsid, :f_defdsts;

      select storid
        from rvcs
       where rvcsid = :f_rvcsid
        into f_storid;

      if (trim(new.efoodid) = '') then
        execute procedure getid(:f_storid, 'EFOOD') returning_values new.efoodid;
      execute procedure sendmessage('RefreshEFood');
    end

  if (inserting or updating) then
    begin
      if (trim(new.landline) = '') then new.landline = null;
      if (new.landline is not null) then
       begin
        new.landline = trim(new.landline);
        new.landline = replace(new.landline, '+30', '');
        new.landline = replace(new.landline, '+357', '');
        if (substring(new.landline from 1 for 4) = '0030') then
         new.landline = substring(new.landline from 5 for char_length(new.landline));
        if (substring(new.landline from 1 for 5) = '00357') then
         new.landline = substring(new.landline from 6 for char_length(new.landline));
       end

      if (trim(new.mobile) = '') then new.mobile = null;
      if (new.mobile is not null) then
       begin
        new.mobile = trim(new.mobile);
        new.mobile = replace(new.mobile, '+30', '');
        new.mobile = replace(new.mobile, '+357', '');
        if (substring(new.mobile from 1 for 4) = '0030') then
         new.mobile = substring(new.mobile from 5 for char_length(new.mobile));
        if (substring(new.mobile from 1 for 5) = '00357') then
         new.mobile = substring(new.mobile from 6 for char_length(new.mobile));
       end
    end

  if (updating) then
    begin
      if (old.status != new.status) then
        execute procedure sendmessage('RefreshEFood');

      if ((old.status != 2) and (new.status = 2)) then
       begin
         select ci1, ci2, id
           from efst
          where efstid = new.storeid
           into :f_defprlsid, :f_defdsts, :f_efstid;

         select rvcsid, useinweborder
           from rvcs
          where rvcsid = (select rvcsid
                            from efst
                           where efstid = new.storeid)
           into :f_rvcsid, :f_useinweborder;

         select first 1 paywid
           from payw
          where upper(trim(altcode)) = upper(trim(new.paymenttype))
           into :f_paywid;

         select first 1 paywid
           from payw
          where upper(trim(altcode)) = 'TIPS'
           into :f_paywtipsid;

         if (:f_paywid is null) then                                 --Check if payment codes are in altcode2 field
          select first 1 paywid
            from payw
           where upper(trim(altcode2)) = upper(trim(new.paymenttype))
            into :f_paywid;

         if (:f_paywtipsid is null) then                             --Check if tips code is in altcode2 field
          select first 1 paywid
            from payw
           where upper(trim(altcode2)) = 'TIPS'
            into :f_paywtipsid;


         if (:d_hotcid is null and new.tblcode is null) then
          select first 1 p.polsid, (select o_prlsid from isvalidprls(p.prlsid, :f_rvcsid, 0))
            from pols p
           where p.rvcsid = :f_rvcsid
            into :f_polsid, :f_prlsid;

         f_defrvcsid = null;
         select first 1 rvcsid
           from rvcs r
                inner join stor s on s.storid = r.storid
                                 and s.isdefault = 1
            into :f_defrvcsid;

         select first 1 syntid
           from synt
          where rvcsid = :f_rvcsid
           into :f_syntid;

         if (new.roomcode is not null) then
           select hotcid
             from hotc
            where roomcode = new.roomcode
              and fiscaldate = (select fiscaldate from gethotcfiscaldate)
             into :d_hotcid;

         if (:d_hotcid is null) then
           if (new.roomno is not null) then
             select first 1 hotcid
               from getroom(new.roomno, :f_rvcsid, 1)
               into :d_hotcid;

         f_custid = null;

         --try to get customer by exact match on phone number
         select first 1 custid
           from cusp
          where (phone = new.mobile) or
                (phone = new.landline)
            into :f_custid;

         --try to get customer by like phone number
         if (:f_custid is null) then
          select first 1 custid
            from cusp
           where (phone like '%' || new.mobile || '%') or
                 (phone like '%' || new.landline || '%')
             into :f_custid;

         if (:f_custid is null and :d_hotcid is null) then
          begin
            execute procedure getid(null, 'CUST') returning_values :f_custid;
            insert into cust
               (custid,
                lname,
                phone,
                cardno,
                city,
                area,
                instype,
                address, strno, zipcode, floor, remarks, maplat, maplng,
                fulladdress,
                custom1, custom2, custom3, custom4, custom5)
            values
              (:f_custid,
               left(new.fullname, 30),
               coalesce(new.mobile, new.landline),
               new.cardno,
               trim(',' from substring(new.city from 1 for position(',', new.city))),
               trim(',' from substring(new.city from position(',', new.city) + 1 for 1000)),
               3, --instype
               left(new.address, 49), '', new.zipcode, new.custfloor, new.bellname, new.latitude, new.longtitude,
               trim(trim(new.address) || ', ' || trim(new.zipcode) || ', ' || trim(new.city)),
               new.ccustom1, new.ccustom2, new.ccustom3, new.ccustom4, new.ccustom5)
            returning custid into :f_custid;

            if (new.landline is not null) then
             insert into cusp
              (custid, phone)
             values (:f_custid, new.landline);

            if ((new.mobile is not null) and (coalesce(new.landline,'') != new.mobile)) then
             insert into cusp
              (custid, phone)
             values (:f_custid, new.mobile);

              f_cusnid = null;
          end
         else
          begin
           select maplat, maplng, fulladdress
             from cust
            where custid = :f_custid
             into :f_latitude, :f_longtitude, :f_fulladdress;

           f_cusnid = null;
           if ((new.latitude is not null) and (new.longtitude is not null)) then
            if ((f_latitude = new.latitude) and (f_longtitude = new.longtitude) and (f_fulladdress = trim(trim(new.address) || ', ' || trim(new.zipcode) || ', ' || trim(new.city)))) then
             f_cusnid = null;
            else
             select first 1 cusnid
               from cusn
              where custid = :f_custid
                and maplat = new.latitude
                and maplng = new.longtitude
                and (fulladdress = trim(trim(coalesce(new.address, '')) || ', ' || trim(coalesce(new.zipcode, '')) || ', ' || trim(coalesce(new.city, ''))))
               into :f_cusnid;

           if (:f_cusnid is null) then
            if (:f_fulladdress = trim(trim(coalesce(new.address, '')) || ', ' || trim(coalesce(new.zipcode, '')) || ', ' || trim(coalesce(new.city, '')))) then
             f_cusnid = null;
            else
             select first 1 cusnid
               from cusn
              where custid = :f_custid
                and (fulladdress = trim(trim(coalesce(new.address, '')) || ', ' || trim(coalesce(new.zipcode, '')) || ', ' || trim(coalesce(new.city, ''))))
               into :f_cusnid;

           if (:f_cusnid is null and :f_custid is not null) then
            begin
             execute procedure getid(null, 'CUSN') returning_values :f_cusnid;
             insert into cusn
               (cusnid,
                custid,
                name,
                city,
                area,
                address,
                strno,
                zipcode,
                floor,
                maplat,
                maplng,
                instype,
                remarks,
                fulladdress)
             values
               (:f_cusnid, --cusnid
                :f_custid, --custid
                left(new.fullname, 30), --name
                trim(',' from substring(new.city from 1 for position(',', new.city))), --city
                trim(',' from substring(new.city from position(',', new.city) + 1 for 1000)), --area
                left(new.address, 49), --address
                '', --strno
                new.zipcode, --zipcode
                new.custfloor, --floor
                new.latitude,
                new.longtitude,
                3, --instype
                new.bellname, --remarks
                trim(trim(new.address) || ', ' || trim(new.zipcode) || ', ' || trim(new.city)));
            end

           if (new.cardno is not null) then
            update cust
               set cardno = new.cardno
             where custid = :f_custid;

           update cust                     --Filling Cust custom fields
              set custom1 = new.ccustom1,
                  custom2 = new.ccustom2,
                  custom3 = new.ccustom3,
                  custom4 = new.ccustom4,
                  custom5 = new.ccustom5
            where custid = :f_custid;
          end

         if (new.uid is not null) then
          begin
           :f_ordmid = cast(:f_efstid || '000' || new.uid as bigint);
          when any do
           :f_ordmid = new.efomid;
          end

         select first 1 useinweborder
           from rvcs
          where storid = (select storid
                            from stor
                           where isdefault = 1)
          order by rvcsid
           into :f_useinweborder2;

         select left(list(name || ' ' || price), 4000) coupons
           from efod
          where price < 0
            and efomid = new.efomid
           into :d_coupons;

         insert into insm
           (otime,
            ordmid,
            code,
            otype,
            usrsid,
            prlsid,
            custid,
            cusnid,
            phone,
            dsts,
            syntid,
            polsid,
            paywid,
            sts,
            hotcid,
            rvcsid,
            tblcode,
            persons,
            remarks,
            paytotal,
            holdtime,
            printtime,
            custom1, custom2, custom3, custom4, custom5)
         values
           (new.orderdatetime, -- otime
            :f_ordmid, -- ordmid
            new.code,
            iif(:d_hotcid is not null or (coalesce(new.tblcode, '') <> ''), 1, 2), -- otype
            coalesce(new.usrsid, 0), -- usrsid
            coalesce(:f_defprlsid,:f_prlsid), -- prlsid
            :f_custid, -- custid
            :f_cusnid, -- cusnid
            coalesce(new.mobile, new.landline), -- phone
            coalesce(:f_defdsts, new.ci1, 1), -- dsts
            :f_syntid, -- syntid
            :f_polsid, -- polsid
            :f_paywid, -- paywid
            0, -- sts
            :d_hotcid,
            iif (:f_useinweborder2 = 1, coalesce(:f_defrvcsid, :f_rvcsid), coalesce(:f_rvcsid, :f_defrvcsid)),
            new.tblcode,
            coalesce(new.persons,0),
            left('#' || trim(new.efoodid) || ' ' ||
                 iif(coalesce(trim(new.acceptmin), '') = '', '', 'ΛΕΠΤΑ: ' || trim(new.acceptmin) || ' ') ||
                 iif(coalesce(trim(new.bellname), '') = '', '', 'ΚΟΥΔΟΥΝΙ: ' || trim(new.bellname)) || ' - ' ||
                 coalesce(new.comments, '') || coalesce(' - ' || :d_coupons, ''), 1000), -- remarks
            new.ordertotal,
            new.holdtime,
            new.printtime,
            new.ocustom1, new.ocustom2, new.ocustom3, new.ocustom4, new.ocustom5
           )
         returning insmid into :f_insmid;
         for select e.efodid, e.storeid, e.code, e.name, e.price, e.qty, e.itmsize, e.sizetype,
                    e.ingredients, e.options, e.itmsid, e.itmvid
               from efod e
                    left outer join efic c on c.eficid = e.eficid
              where e.efomid = new.efomid
                and coalesce(c.correlate, 1) <> 0
                and e.price >= 0
               into :d_efodid, :d_storeid, :d_code, :d_name, :d_price, :d_qty, :d_itmsize, :d_sizetype,
                    :d_ingredients, :d_options, :d_itmsid, :d_itmvid do
           begin
             -- check if efic has value in name
             /*
             select name
               from efic
              where code = :d_code
                and storeid = :d_storeid
               into :f_eficname;

             if ((f_eficname is null) or (f_eficname != d_name)) then
              update efic
                 set name = :d_name
               where code = :d_code
                 and storeid = :d_storeid;
             */
             -- check if the item is dicount/tips in order to inserted on insp table
             if ((d_code = '-1') and (position('TIP ', d_name) > 0) and (d_price > 0)) then
              begin
               insert into insp
                 (insmid, paywid, amount, tips)
               values
                 (:f_insmid, :f_paywtipsid, 0, :d_price);
              end
             else -- insert item to insd as it is a normal item
              begin
               select name
                from itms
               where itmsid = :d_itmsid
                into :d_name;
               -- insert main item
               /*d_itmvid = null;
               select itmvid
                 from itmv
                where upper(trim(code)) = upper(trim(:d_sizetype))
                   or upper(trim(name)) = upper(trim(:d_sizetype))
                 into :d_itmvid;
               if (:d_itmvid is null) then
                 select itmvid
                   from itmv
                  where upper(trim(code)) = upper(trim(:d_itmsize))
                     or upper(trim(name)) = upper(trim(:d_itmsize))
                   into :d_itmvid;*/
               if (:d_itmvid is null) then
                 d_itmvid = 0;

               execute procedure getid(null, 'ORDD') returning_values :d_efodid;
               insert into insd
                 (insmid, orddid, parentid, itmsid, itmvid, name, qty, price, isextra, psts)
               values
                 (:f_insmid, :d_efodid, :d_efodid, :d_itmsid, :d_itmvid, substring(:d_name from 1 for 30), :d_qty, :d_price, 0, 1)
               returning insdid into :d_insdid;
               -- insert extra item ingredients
               if (trim(:d_ingredients) != '') then
                 begin
                   for select o_string from get_delimited(:d_ingredients, '|', 30) into :d_string do
                    if (trim(d_string) != '') then
                     begin
                       execute procedure getid(null, 'ORDD') returning_values :d_temp;
                       insert into insd
                         (insmid, orddid, parentid, itmsid, name, qty, price, isextra, psts)
                       values
                         (:f_insmid, :d_temp, :d_efodid, 0, substring(replace(:d_string, ascii_char(13), '') from 1 for 30), :d_qty, 0, 1, 1);
                     end
                 end
               -- insert extra item options
               if (trim(:d_options) != '') then
                 begin
                   for select o_string from get_delimited(:d_options, '|', 30) into :d_string do
                    if (trim(d_string) != '') then
                     begin
                       execute procedure getid(null, 'ORDD') returning_values :d_temp;
                       insert into insd
                         (insmid, orddid, parentid, itmsid, name, qty, price, isextra, psts)
                       values
                         (:f_insmid, :d_temp, :d_efodid, 0, substring(replace(:d_string, ascii_char(13), '') from 1 for 30), :d_qty, 0, 1, 1);
                     end
                 end
              end
           end

         for select ptype, paywid, amount - iif(ptype = 2, 0, iif(new.apptype in (6, 7), 0, coalesce((select sum(tips) from insp where insmid in (select distinct insmid from insm where ordmid = efop.efomid)), 0)))
               from efop
             where efomid = new.efomid
               into f_ptype, f_paywid, f_amount do
           if (f_ptype = 2) then --tips
             insert into insp
               (insmid, paywid, amount, tips, hotcid)
             values
               (:f_insmid, :f_paywid, 0, :f_amount, :d_hotcid);
           else
             insert into insp
               (insmid, paywid, amount, hotcid)
             values
               (:f_insmid, :f_paywid, :f_amount, :d_hotcid);

          update insm
            set paytotal = iif(paytotal > (select sum(qty*price) from insd where insmid = :f_insmid), null, paytotal)
          where insmid = :f_insmid;

         update insm
            set sts = 2
          where insmid = :f_insmid;

         select newordmid
           from insm
          where insmid = :f_insmid
           into new.ordmid;

         if (f_useinweborder <> 1) then
           begin
             insert into bufp (ordmid) values (new.ordmid);
             execute procedure sendmessage('PrintOrder');
           end
       end /* new status is 2 */
    end
end