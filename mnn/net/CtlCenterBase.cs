﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace mnn.net {
    public class CtlCenterBase {
        // session control
        protected SessCtl sessctl;
        // other request control
        protected DispatcherBase dispatcher;

        public CtlCenterBase()
        {
            // init sesscer
            sessctl = new SessCtl();
            sessctl.sess_parse += new SessCtl.SessParseDelegate(sess_parse);
            sessctl.sess_create += new SessCtl.SessCreateDelegate(sess_create);
            sessctl.sess_delete += new SessCtl.SessDeleteDelegate(sess_delete);

            // init dispatcher
            dispatcher = new DispatcherBase();
        }

        // Session Event ==================================================================================

        protected virtual void sess_parse(object sender, SockSess sess)
        {
            // init request & response
            SockRequest request = new SockRequest() { lep = sess.lep, rep = sess.rep };
            SockResponse response = new SockResponse() { data = null };
            byte[] data = sess.rdata.Take(sess.rdata_size).ToArray();

            // check request
            if (!request.CheckType(data)) {
                sess.RfifoSkip(sess.rdata_size);
                request.type = SockRequestType.unknown;
                request.length = -1;
                request.data = data;
            } else if (request.CheckLength(data))
                sess.RfifoSkip(request.ParseRawData(data));
            else
                return;

            // dispatch
            dispatcher.handle(request, response);
            if (response.data != null && response.data.Length != 0)
                sessctl.SendSession(sess, response.data);
        }

        protected virtual void sess_create(object sender, SockSess sess) { }

        protected virtual void sess_delete(object sender, SockSess sess) { }
    }
}