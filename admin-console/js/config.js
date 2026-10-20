// admin-console 公共配置；微信凭据由服务端从部署环境读取。
//
// 这个文件是 UMD 的：浏览器里挂在 window.WCB_CONFIG，Node 里可 require，
// 这样 test/smoke.mjs 能直接读同一份配置跑契约冒烟。
//
// baseURL 的优先级：页面「设置」里的运行时覆盖（localStorage） > 本文件。
// 留空时使用管理页面上一级的同源 API，支持 /admin/ 和 /prefix/admin/。
(function (root, factory) {
  const config = factory();
  if (typeof module === "object" && module.exports) {
    module.exports = config;
  } else {
    root.WCB_CONFIG = config;
  }
})(typeof self !== "undefined" ? self : globalThis, function () {
  return {
    // 无需在前端填写 AppID / AppSecret，也不要将 .env 放入静态目录。
    baseURL: "",

    // 待确认列表自动刷新间隔（毫秒）。卖家盯着页面等买家登记，10 秒一拍。
    autoRefreshMs: 10000,

    // 本地操作日志上限（条）。只存本浏览器产生的动作，超过后丢最旧的。
    actionLogLimit: 200,
  };
});
